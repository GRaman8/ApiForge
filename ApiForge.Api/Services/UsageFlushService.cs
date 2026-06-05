using ApiForge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ApiForge.Api.Services;

// Background service that flushes buffered usage events in batches instead of writing to the
// database on every request. It also updates ApiKey.LastUsedAt in the same batch on a fresh
// scoped DbContext (this replaces the fire-and-forget-on-a-scoped-context bug from the blueprint).
public class UsageFlushService(IServiceScopeFactory scopeFactory, UsageQueue queue, ILogger<UsageFlushService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var events = queue.Drain();
            if (events.Count == 0) continue;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                db.UsageEvents.AddRange(events);

                foreach (var grp in events.GroupBy(e => e.ApiKeyId))
                {
                    var last = grp.Max(e => e.RequestedAt);
                    await db.ApiKeys
                        .Where(k => k.Id == grp.Key)
                        .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, last), ct);
                }

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to flush {Count} usage events", events.Count);
            }
        }
    }
}
