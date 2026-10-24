namespace Olive.Application;

// UI-05: read-only audit port. Tenant-scoped; no write member exists.
public interface IAuditStore
{
    Task<AuditRecord?> GetAsync(Guid tenantId, Guid proposalId, CancellationToken ct);
}

public sealed record AuditRecord(Guid ProposalId, string ActionType, string PartyRef, string ParametersJson, string Justification, string BodySha256, string ProposalHash, string TraceVerification, ChainRow? Chain);

public sealed record ChainRow(string? PolicyDecisionJson, string? DecidedBy, string? AuthSource, DateTimeOffset? DecidedAt, string? Rationale, string ExecutionMode, DateTimeOffset StartedAt, string? Outcome, string? Reason, DateTimeOffset? FinishedAt);
