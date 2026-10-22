using System.Reflection;
using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

// One record per outbound port call: which port, where it may go, and what credential it carried (plan 03-06 containment checks).
public sealed record OutboundCall(string Port, OutboundDestination Destination, bool CarriedSessionKey, bool CarriedServiceToken);

public sealed class RecordingTraceResolver(ITraceResolver inner, List<string> events, List<OutboundCall> calls) : ITraceResolver, IComponentDecorator
{
    private static readonly OutboundDestination Destination =
        typeof(ITraceResolver).GetCustomAttribute<OutboundPortAttribute>()!.Destination;

    public object Inner => inner;

    public Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct)
    {
        lock (events) { events.Add("resolve"); }
        lock (calls) { calls.Add(new OutboundCall(nameof(ITraceResolver), Destination, false, grant is not null)); }
        return inner.ResolveAsync(grant!, traceId, ct);
    }
}

public sealed class RecordingTokenIssuer(ITokenIssuer inner, List<string> events, List<OutboundCall> calls) : ITokenIssuer, IComponentDecorator
{
    private static readonly OutboundDestination Destination =
        typeof(ITokenIssuer).GetCustomAttribute<OutboundPortAttribute>()!.Destination;

    public object Inner => inner;

    public Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct)
    {
        lock (events) { events.Add("token"); }
        lock (calls) { calls.Add(new OutboundCall(nameof(ITokenIssuer), Destination, key is not null, false)); }
        return inner.IssueAsync(key!, ct);
    }
}
