namespace Olive.Application;

public interface IProposalStore
{
    // Returns null on a duplicate (tenant, agent, run) instead of throwing (D-10).
    Task<Guid?> InsertAsync(Guid tenantId, NewProposal proposal, CancellationToken ct);

    // Cross-tenant lookup is indistinguishable from missing (D-11).
    Task<ProposalRecord?> GetAsync(Guid tenantId, Guid proposalId, CancellationToken ct);

    // Tenant-scoped idempotency lookup by run key; returns current state and stored body hash (null for legacy rows).
    Task<ProposalRunRecord?> FindByRunAsync(Guid tenantId, string agentId, string runId, CancellationToken ct);

    // Compare-and-set on state; false when from_state does not match or the row is not in this tenant.
    Task<bool> TransitionAsync(Guid tenantId, Guid proposalId, string fromState, string toState, string actor, CancellationToken ct);
}

public sealed record NewProposal(string AgentId, string KeyId, string OnBehalfOf, string Purpose, DateTimeOffset KeyExpiresAt, string? NonoSessionId, string RunId, string? ActionType, string? PartyRef, string? ParametersJson, string? Justification, string? TraceRef, string? CanonicalBody = null, string? BodySha256 = null, string? ProposalHash = null);

public sealed record ProposalRunRecord(Guid ProposalId, string State, string? BodySha256);

public sealed record ProposalRecord(Guid ProposalId, Guid TenantId, string State, string AgentId, string KeyId, string OnBehalfOf, string Purpose, DateTimeOffset KeyExpiresAt, string? NonoSessionId, string RunId, string? ActionType, string? PartyRef, string? ParametersJson, string? Justification, string? TraceRef, DateTimeOffset CreatedAt);
