using System.Collections.Concurrent;
using ApiForge.Api.Domain;

namespace ApiForge.Api.Services;

// Thread-safe in-memory buffer of usage events. Registered as a singleton so the middleware
// and the background flusher share one instance.
//
// Tradeoff: at-most-once. Up to one flush interval (~5s) of events can be lost on a task
// restart/deploy/crash. Acceptable for analytics; not for billing.
public class UsageQueue
{
    private readonly ConcurrentQueue<UsageEvent> _queue = new();

    public void Enqueue(UsageEvent e) => _queue.Enqueue(e);

    public IReadOnlyList<UsageEvent> Drain()
    {
        var items = new List<UsageEvent>();
        while (_queue.TryDequeue(out var item)) items.Add(item);
        return items;
    }
}
