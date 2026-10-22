using Olive.Application;
using static Olive.Tests.TraceBuilders;

namespace Olive.Tests;

public sealed class CachingTraceResolverTests
{
    private static TraceLookup.Found Found() => new(Facts(), "hometown");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_08_two_found_lookups_hit_inner_once()
    {
        var inner = new FakeTraceResolver(Found(), Found());
        var c = new CachingTraceResolver(inner);
        var first = await c.ResolveAsync(Grant(), "t1", TestContext.Current.CancellationToken);
        var second = await c.ResolveAsync(Grant(), "t1", TestContext.Current.CancellationToken);
        Assert.Equal(1, inner.Calls);
        Assert.Equal("hometown", Assert.IsType<TraceLookup.Found>(second).Source);
        Assert.Equal(first, second);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_08_not_found_then_found_hits_inner_twice()
    {
        var inner = new FakeTraceResolver(new TraceLookup.NotFound(), Found());
        var c = new CachingTraceResolver(inner);
        Assert.IsType<TraceLookup.NotFound>(await c.ResolveAsync(Grant(), "t1", TestContext.Current.CancellationToken));
        Assert.IsType<TraceLookup.Found>(await c.ResolveAsync(Grant(), "t1", TestContext.Current.CancellationToken));
        Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    [Trait("Category", "Unit")]
    public async Task TRC_08_failures_are_never_cached(TraceLookup failure)
    {
        var inner = new FakeTraceResolver(failure, failure);
        var c = new CachingTraceResolver(inner);
        await c.ResolveAsync(Grant(), "t1", TestContext.Current.CancellationToken);
        await c.ResolveAsync(Grant(), "t1", TestContext.Current.CancellationToken);
        Assert.Equal(2, inner.Calls);
    }

    public static IEnumerable<object[]> Failures() =>
    [
        [new TraceLookup.Unavailable()],
        [new TraceLookup.CallerRejected(401)],
        [new TraceLookup.CallerRejected(403)],
        [new TraceLookup.Malformed()],
    ];

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_08_cache_is_keyed_by_tenant()
    {
        var inner = new FakeTraceResolver(Found(), Found());
        var c = new CachingTraceResolver(inner);
        await c.ResolveAsync(Grant(), "t1", TestContext.Current.CancellationToken);
        await c.ResolveAsync(Grant(tenant: Guid.NewGuid()), "t1", TestContext.Current.CancellationToken);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_08_cap_clears_without_changing_results()
    {
        var results = Enumerable.Range(0, 10_003).Select(_ => (TraceLookup)Found()).ToArray();
        var inner = new FakeTraceResolver(results);
        var c = new CachingTraceResolver(inner);
        for (var i = 0; i < 10_002; i++)
        {
            Assert.IsType<TraceLookup.Found>(await c.ResolveAsync(Grant(), $"t{i}", TestContext.Current.CancellationToken));
        }

        // t0 was cleared when the cache filled, so it resolves through inner again with the same result.
        Assert.IsType<TraceLookup.Found>(await c.ResolveAsync(Grant(), "t0", TestContext.Current.CancellationToken));
        Assert.Equal(10_003, inner.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_08_decorator_exposes_inner_for_production_guard()
    {
        var stub = new MarkedResolver();
        IComponentDecorator c = new CachingTraceResolver(stub);
        Assert.Same(stub, c.Inner);
        Assert.IsAssignableFrom<INonProductionOnly>(c.Inner);
    }

    private sealed class MarkedResolver : ITraceResolver, INonProductionOnly
    {
        public Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct) =>
            Task.FromResult<TraceLookup>(new TraceLookup.NotFound());
    }
}
