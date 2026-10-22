namespace Olive.Application;

public sealed record TraceFacts(string TraceId, string SchemaVersion, string? RunId, Guid TenantId, string? AgentId, string? ProposalHash, DateTimeOffset CreatedAt);

public abstract record TraceLookup
{
    public sealed record Found(TraceFacts Facts, string Source) : TraceLookup;
    public sealed record NotFound : TraceLookup;
    public sealed record Unavailable : TraceLookup;
    public sealed record CallerRejected(int Status) : TraceLookup;
    public sealed record Malformed : TraceLookup;
}

// D17.1: takes the service token grant only; it cannot accept a session key by construction.
[OutboundPort(OutboundDestination.Hometown)]
public interface ITraceResolver { Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct); }
