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

    // IRQ-02 / D-09: identity_review decisions. accept and reject decide the item; defer returns it to open with the claim cleared.
    // No proposal is touched. decided_at is stamped from the store's TimeProvider inside the transaction after the row lock.
    Task<DecideResult> DecideIdentityAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, DateTimeOffset now, CancellationToken ct);

    // IRQ-01/IRQ-02: idempotent insert keyed on (tenant, producer, producer_ref). Same body hash replays the current state; a different hash conflicts.
    // IRQ-03 / D-13..D-16: decided accept/reject identity items of one producer in one tenant, oldest first by (decided_at, item_id). Single read, no lock.
    Task<IReadOnlyList<QueueItemDetail>> ListDecidedForProducerAsync(Guid tenantId, string producer, DateTime? afterAtUtc, Guid? afterItemId, int limit, CancellationToken ct);

    // IRQ-03 / D-17: producer withdraw of an undecided identity item. The claim is cleared and the event written in one transaction.
    Task<WithdrawResult> WithdrawByProducerAsync(Guid tenantId, string producer, Guid itemId, string reason, CancellationToken ct);

    Task<CreateIdentityResult> CreateIdentityItemAsync(Guid tenantId, string producer, string kind, string subtype, Guid producerRef, string subjectRef, string payloadRef, string rulesetVersion, string bodySha256, DateTimeOffset now, CancellationToken ct);
}

public abstract record WithdrawResult
{
    public sealed record Withdrawn(Guid ItemId) : WithdrawResult;

    public sealed record AlreadyWithdrawn(Guid ItemId) : WithdrawResult;

    public sealed record Closed : WithdrawResult;

    public sealed record NotFound : WithdrawResult;

    public sealed record InvalidRequest : WithdrawResult;
}

public abstract record CreateIdentityResult
{
    public sealed record Created(Guid ItemId, string State) : CreateIdentityResult;

    public sealed record Replay(Guid ItemId, string State) : CreateIdentityResult;

    public sealed record Conflict : CreateIdentityResult;
}

public abstract record DecideResult
{
    public sealed record Decided(QueueItemDetail Item) : DecideResult;

    public sealed record AlreadyDecidedSame(QueueItemDetail Item) : DecideResult;

    public sealed record Deferred(QueueItemDetail Item) : DecideResult;

    public sealed record Withdrawn : DecideResult;

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

    public sealed record Withdrawn : ReleaseResult;

    public sealed record ProposalExpired : ReleaseResult;

    public sealed record Closed : ReleaseResult;

    public sealed record NotFound : ReleaseResult;
}

public sealed record QueueItemSummary(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? RulesetVersion = null);

// No rationale member: the rationale can never be echoed from a detail record.
public sealed record QueueItemDetail(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? Decision, string? DecidedBy, DateTimeOffset? DecidedAt, string? AuthSource, string? WithdrawnReason, Guid? ProposalId, string? RulesetVersion = null);

public sealed record QueueItemEventRecord(long Seq, string EventType, string? FromState, string ToState, string Actor, DateTimeOffset CreatedAt, string? DetailJson);

public sealed record ReviewerPayload(Guid ItemId, string Kind, string? ActionType, string? ParametersJson, string? Justification, string OnBehalfOf, DateTimeOffset? ExpiresAt, string? TraceRef);
