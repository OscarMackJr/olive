using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Olive.Application;
using Olive.Domain;

namespace Olive.Integrations.Hometown;

// Fixture-backed stand-in for hometown trace resolution. Non-production only (TRC-09, FND-05).
// Returns fixtures verbatim and does not tenant-scope lookups (D-17): live hometown 404s cross-tenant lookups.
public sealed class FixtureTraceResolver : ITraceResolver, INonProductionOnly
{
    private readonly ConcurrentDictionary<string, TraceFacts> _facts = new(StringComparer.Ordinal);

    public static FixtureTraceResolver CreateDefault()
    {
        var resolver = new FixtureTraceResolver();
        var asm = typeof(FixtureTraceResolver).Assembly;
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(asm, "trace-resolution/index.json"))
            ?? throw new InvalidOperationException("Trace fixture index is malformed.");
        foreach (var (traceId, file) in index)
        {
            if (TraceResolutionResponse.Parse(ReadResource(asm, "trace-resolution/" + file)) is not TraceLookup.Found found)
            {
                throw new InvalidOperationException("A trace fixture is malformed.");
            }

            resolver.Register(traceId, found.Facts);
        }

        return resolver;
    }

    public void Register(string traceId, TraceFacts facts) => _facts[traceId] = facts;

    public Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        TraceLookup result = _facts.TryGetValue(traceId, out var facts)
            ? new TraceLookup.Found(facts, TraceVerificationSources.Fixture)
            : new TraceLookup.NotFound();
        return Task.FromResult(result);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException("A trace fixture resource is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
