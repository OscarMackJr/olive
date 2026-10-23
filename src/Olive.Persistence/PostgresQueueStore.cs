using Dapper;
using Olive.Application;

namespace Olive.Persistence;

// Read side only; no method writes. Business outcomes are null/empty results, never exceptions.
public sealed class PostgresQueueStore(TenantUnitOfWork uow) : IQueueStore
{
    static PostgresQueueStore() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private sealed class ItemRow
    {
        public Guid TenantId { get; set; }
        public Guid ItemId { get; set; }
        public string Kind { get; set; } = "";
        public string Subtype { get; set; } = "";
        public string Producer { get; set; } = "";
        public Guid ProducerRef { get; set; }
        public string State { get; set; } = "";
        public string SubjectRef { get; set; } = "";
        public string PayloadRef { get; set; } = "";
        public string? ClaimedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? Decision { get; set; }
        public string? DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        public string? AuthSource { get; set; }
        public string? WithdrawnReason { get; set; }
        public Guid? ProposalId { get; set; }
    }

    private sealed class EventRow
    {
        public long Seq { get; set; }
        public string EventType { get; set; } = "";
        public string? FromState { get; set; }
        public string ToState { get; set; } = "";
        public string Actor { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? DetailJson { get; set; }
    }

    private sealed class PayloadRow
    {
        public Guid ItemId { get; set; }
        public string Kind { get; set; } = "";
        public string? ActionType { get; set; }
        public string? ParametersJson { get; set; }
        public string? Justification { get; set; }
        public string OnBehalfOf { get; set; } = "";
        public DateTime? ExpiresAt { get; set; }
        public string? TraceRef { get; set; }
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? d) => d is { } v ? Utc(v) : null;

    public Task<IReadOnlyList<QueueItemSummary>> ListAsync(Guid tenantId, string? kind, string? state, DateTimeOffset now, int limit, CancellationToken ct) =>
        uow.ExecuteAsync<IReadOnlyList<QueueItemSummary>>(tenantId, async (conn, tx) =>
        {
            var rows = await conn.QueryAsync<ItemRow>(new CommandDefinition(QueueSql.List,
                new { tenant_id = tenantId, kind, state, now = now.UtcDateTime, limit }, tx, cancellationToken: ct));
            return rows.Select(r => new QueueItemSummary(r.TenantId, r.ItemId, r.Kind, r.Subtype, r.Producer, r.ProducerRef, r.State,
                r.SubjectRef, r.PayloadRef, r.ClaimedBy, Utc(r.CreatedAt), Utc(r.ExpiresAt))).ToList();
        }, ct);

    public Task<QueueItemDetail?> GetAsync(Guid tenantId, Guid itemId, CancellationToken ct) =>
        uow.ExecuteAsync<QueueItemDetail?>(tenantId, async (conn, tx) =>
        {
            var r = await conn.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(QueueSql.GetItem,
                new { tenant_id = tenantId, item_id = itemId }, tx, cancellationToken: ct));
            return r is null ? null : new QueueItemDetail(r.TenantId, r.ItemId, r.Kind, r.Subtype, r.Producer, r.ProducerRef, r.State,
                r.SubjectRef, r.PayloadRef, r.ClaimedBy, Utc(r.CreatedAt), Utc(r.ExpiresAt), r.Decision, r.DecidedBy, Utc(r.DecidedAt),
                r.AuthSource, r.WithdrawnReason, r.ProposalId);
        }, ct);

    public Task<IReadOnlyList<QueueItemEventRecord>?> EventsAsync(Guid tenantId, Guid itemId, CancellationToken ct) =>
        uow.ExecuteAsync<IReadOnlyList<QueueItemEventRecord>?>(tenantId, async (conn, tx) =>
        {
            var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(QueueSql.ItemExists,
                new { tenant_id = tenantId, item_id = itemId }, tx, cancellationToken: ct));
            if (!exists)
            {
                return null;
            }

            var rows = await conn.QueryAsync<EventRow>(new CommandDefinition(QueueSql.Events,
                new { tenant_id = tenantId, item_id = itemId }, tx, cancellationToken: ct));
            return rows.Select(e => new QueueItemEventRecord(e.Seq, e.EventType, e.FromState, e.ToState, e.Actor, Utc(e.CreatedAt), e.DetailJson)).ToList();
        }, ct);

    public Task<ReviewerPayload?> GetPayloadAsync(Guid tenantId, Guid itemId, CancellationToken ct) =>
        uow.ExecuteAsync<ReviewerPayload?>(tenantId, async (conn, tx) =>
        {
            var r = await conn.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(QueueSql.Payload,
                new { tenant_id = tenantId, item_id = itemId }, tx, cancellationToken: ct));
            return r is null ? null : new ReviewerPayload(r.ItemId, r.Kind, r.ActionType, r.ParametersJson, r.Justification, r.OnBehalfOf, Utc(r.ExpiresAt), r.TraceRef);
        }, ct);
}
