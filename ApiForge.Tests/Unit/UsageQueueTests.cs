using ApiForge.Api.Domain;
using ApiForge.Api.Services;

namespace ApiForge.Tests.Unit;

public class UsageQueueTests
{
    [Fact]
    public void Drain_returns_all_enqueued_events_and_empties_the_queue()
    {
        var queue = new UsageQueue();
        queue.Enqueue(new UsageEvent { Endpoint = "GET /items" });
        queue.Enqueue(new UsageEvent { Endpoint = "POST /items" });

        var first = queue.Drain();
        Assert.Equal(2, first.Count);

        var second = queue.Drain();
        Assert.Empty(second);
    }

    [Fact]
    public async Task Enqueue_is_thread_safe()
    {
        var queue = new UsageQueue();
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(i =>
            Task.Run(() => queue.Enqueue(new UsageEvent { Endpoint = $"GET /{i}" }))));

        Assert.Equal(1000, queue.Drain().Count);
    }
}
