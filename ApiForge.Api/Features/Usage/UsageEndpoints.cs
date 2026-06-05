using ApiForge.Api.Data;
using ApiForge.Api.Features.Keys;
using Microsoft.EntityFrameworkCore;

namespace ApiForge.Api.Features.Usage;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/keys/{id:guid}/usage", async (
            Guid id, AppDbContext db, HttpContext ctx, DateTime? from, DateTime? to) =>
        {
            var userId = ctx.User.UserId();
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
            if (key is null) return Results.NotFound();

            var query = db.UsageEvents.Where(e => e.ApiKeyId == id);
            if (from.HasValue) query = query.Where(e => e.RequestedAt >= from.Value.ToUniversalTime());
            if (to.HasValue) query = query.Where(e => e.RequestedAt <= to.Value.ToUniversalTime());

            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var events = await query.ToListAsync();

            return Results.Ok(new
            {
                keyId = id,
                totalRequests = events.Count,
                requestsToday = events.Count(e => e.RequestedAt >= today),
                requestsThisMonth = events.Count(e => e.RequestedAt >= monthStart),
                byEndpoint = events
                    .GroupBy(e => e.Endpoint)
                    .Select(g => new { endpoint = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count)
                    .ToList(),
                averageLatencyMs = events.Count > 0 ? (int)events.Average(e => e.LatencyMs) : 0
            });
        })
        .WithTags("Usage")
        .RequireAuthorization();
    }
}
