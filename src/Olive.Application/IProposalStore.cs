namespace Olive.Application;

public interface IProposalStore
{
    // Returns null on a duplicate (tenant, agent, run) instead of throwing (D-10).
    Task<Guid?> InsertAsync(Guid tenantId, NewProposal proposal, CancellationToken ct);

    // D-08..D-12: the accepted-path insert. In ONE tenant transaction: advisory lock on (tenant, agent, action), count non-refused rows
    // in the sliding window (before the insert), then either accept (awaiting_approval, allowed policy_evaluated event, expires_at kept)
    // or store a rate_limited refusal (expires_at null). Returns null on a duplicate (tenant, agent, run).
    Task<EvaluatedInsertResult?> InsertEvaluatedAsync(Guid tenantId, NewProposal proposal, int rateLimit, int windowSeconds, Olive.Domain.PolicyVerdict priorPassing, CancellationToken ct);

    // The single refusal-writing path, reused by Phase 4 policy refusals (D-05): insert then compare-and-set to refused_policy with
    // the reason, plus "submitted" and "trace_refused" events, in one tenant transaction. Returns null on a duplicate (tenant, agent, run).
    // When policyDetailJson is given the refusal is a policy refusal: a "policy_evaluated" event carries that static detail and the
    // transition event is "policy_refused"; when null it is a trace refusal ("trace_refused").
    Task<Guid?> InsertRefusedAsync(Guid tenantId, NewProposal proposal, string refusalReason, CancellationToken ct, string? policyDetailJson = null);

    // Cross-tenant lookup is indistinguishable from missing (D-11).
    Task<ProposalRecord?> GetAsync(Guid tenantId, Guid proposalId, CancellationToken ct);

    // Tenant-scoped idempotency lookup by run key; returns current state, stored body hash (null for legacy rows) and the
    // delegation context (on_behalf_of, purpose) the row was created under, which the body hash does not cover (WR-02).
    Task<ProposalRunRecord?> FindByRunAsync(Guid tenantId, string agentId, string runId, CancellationToken ct);

    // Compare-and-set on state; false when from_state does not match or the row is not in this tenant.
    // Guarded by ProposalStates.CanTransition (POL-06): an edge outside the table throws ArgumentException before any database work.
    // Throws ArgumentException when either state is refused_policy: refusals go through InsertRefusedAsync only (WR-07).
    Task<bool> TransitionAsync(Guid tenantId, Guid proposalId, string fromState, string toState, string actor, CancellationToken ct);
}

public sealed record NewProposal(string AgentId, string KeyId, string OnBehalfOf, string Purpose, DateTimeOffset KeyExpiresAt, string? NonoSessionId, string RunId, string? ActionType, string? PartyRef, string? ParametersJson, string? Justification, string? TraceRef, string? CanonicalBody = null, string? BodySha256 = null, string? ProposalHash = null, string? TraceVerification = null, string? TraceSchemaVersion = null, DateTimeOffset? ExpiresAt = null);

public sealed record EvaluatedInsertResult(Guid ProposalId, string State, string? RefusalReason);

public sealed record ProposalRunRecord(Guid ProposalId, string State, string? BodySha256, string OnBehalfOf, string Purpose, string? RefusalReason = null);

public sealed record ProposalRecord(Guid ProposalId, Guid TenantId, string State, string AgentId, string KeyId, string OnBehalfOf, string Purpose, DateTimeOffset KeyExpiresAt, string? NonoSessionId, string RunId, string? ActionType, string? PartyRef, string? ParametersJson, string? Justification, string? TraceRef, DateTimeOffset CreatedAt, string? RefusalReason = null, string? TraceVerification = null, string? TraceSchemaVersion = null, DateTimeOffset? ExpiresAt = null);
