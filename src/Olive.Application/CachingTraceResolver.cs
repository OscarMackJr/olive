using System.Collections.Concurrent;

namespace Olive.Application;

// TRC-08, D16.4: an optimisation only. Positive results are cached by (tenant, trace_id); every other outcome is never cached.
public sealed class CachingTraceResolver(ITraceResolver inner) : ITraceResolver, IComponentDecorator
{
    private const int Capacity = 10_000;
    private readonly ConcurrentDictionary<(Guid TenantId, string TraceId), TraceLookup.Found> _cache = new();

    public object Inner => inner;

    public async Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct)
    {
        var key = (grant.TenantId, traceId);
        if (_cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var result = await inner.ResolveAsync(grant, traceId, ct);
        if (result is TraceLookup.Found found)
        {
            if (_cache.Count >= Capacity)
            {
                _cache.Clear();
            }

            _cache[key] = found;
        }

        return result;
    }
}
