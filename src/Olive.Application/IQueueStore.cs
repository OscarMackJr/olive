namespace Olive.Application;

// Read side of the generic review queue (D-14). Business outcomes are result values, never exceptions, so a later unit of work can
// commit lazy expiry before a 409 is returned. Later plans append claim, release and decide members.
public interface IQueueStore
{
    // Tenant-predicated, oldest first. Open or claimed items whose expiry is at or before now are omitted (fail closed); no write.
    Task<IReadOnlyList<QueueItemSummary>> ListAsync(Guid tenantId, string? kind, string? state, DateTimeOffset now, int limit, CancellationToken ct);

    Task<QueueItemDetail?> GetAsync(Guid tenantId, Guid itemId, CancellationToken ct);

    // Ordered by seq (QUE-09). Null when the item does not exist in the tenant.
    Task<IReadOnlyList<QueueItemEventRecord>?> EventsAsync(Guid tenantId, Guid itemId, CancellationToken ct);

    Task<ReviewerPayload?> GetPayloadAsync(Guid tenantId, Guid itemId, CancellationToken ct);

    // QUE-05: one winner under concurrency. D-11: an expired proposal is withdrawn and committed, then ProposalExpired is returned.
    Task<ClaimResult> ClaimAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct);

    // QUE-06: claimant only. D-20: release also triggers lazy expiry.
    Task<ReleaseResult> ReleaseAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct);

    // APR-04: item decision and proposal transition in one transaction. QUE-08: idempotent for the same actor and decision.
    // REQ-APR-02 / CR-01: approveIdentityVerified is true only when the caller ran the approval identity check and it returned Allowed.
    // Under the row lock an approve with the flag false is never applied; a claim that landed after the caller's pre-read yields NotClaimed
    // so the client retries and the check then runs. Reject is unaffected (D-18).
    Task<DecideResult> DecideAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, bool approveIdentityVerified, DateTimeOffset now, CancellationToken ct);
}

public abstract record DecideResult
{
    public sealed record Decided(QueueItemDetail Item) : DecideResult;

    public sealed record AlreadyDecidedSame(QueueItemDetail Item) : DecideResult;

    public sealed record Conflict : DecideResult;

    public sealed record NotClaimant : DecideResult;

    public sealed record NotClaimed : DecideResult;

    public sealed record Closed : DecideResult;

    public sealed record ProposalExpired : DecideResult;

    public sealed record NotFound : DecideResult;
}

public abstract record ClaimResult
{
    public sealed record Claimed(QueueItemDetail Item) : ClaimResult;

    public sealed record AlreadyClaimed : ClaimResult;

    public sealed record ProposalExpired : ClaimResult;

    public sealed record Closed : ClaimResult;

    public sealed record NotFound : ClaimResult;
}

public abstract record ReleaseResult
{
    public sealed record Released(QueueItemDetail Item) : ReleaseResult;

    public sealed record NotClaimant : ReleaseResult;

    public sealed record NotClaimed : ReleaseResult;

    public sealed record ProposalExpired : ReleaseResult;

    public sealed record Closed : ReleaseResult;

    public sealed record NotFound : ReleaseResult;
}

public sealed record QueueItemSummary(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

// No rationale member: the rationale can never be echoed from a detail record.
public sealed record QueueItemDetail(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? Decision, string? DecidedBy, DateTimeOffset? DecidedAt, string? AuthSource, string? WithdrawnReason, Guid? ProposalId);

public sealed record QueueItemEventRecord(long Seq, string EventType, string? FromState, string ToState, string Actor, DateTimeOffset CreatedAt, string? DetailJson);

public sealed record ReviewerPayload(Guid ItemId, string Kind, string? ActionType, string? ParametersJson, string? Justification, string OnBehalfOf, DateTimeOffset? ExpiresAt, string? TraceRef);
