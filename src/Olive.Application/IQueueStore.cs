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
}

public sealed record QueueItemSummary(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

// No rationale member: the rationale can never be echoed from a detail record.
public sealed record QueueItemDetail(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? Decision, string? DecidedBy, DateTimeOffset? DecidedAt, string? AuthSource, string? WithdrawnReason, Guid? ProposalId);

public sealed record QueueItemEventRecord(long Seq, string EventType, string? FromState, string ToState, string Actor, DateTimeOffset CreatedAt, string? DetailJson);

public sealed record ReviewerPayload(Guid ItemId, string Kind, string? ActionType, string? ParametersJson, string? Justification, string OnBehalfOf, DateTimeOffset? ExpiresAt, string? TraceRef);
