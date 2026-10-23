using Dapper;
using Npgsql;
using Olive.Application;
using Olive.Domain;

namespace Olive.Persistence;

// Business outcomes are result values, never exceptions, so lazy expiry commits before a 409 is returned (POL-08).
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

    private static QueueItemDetail ToDetail(ItemRow r) => new(r.TenantId, r.ItemId, r.Kind, r.Subtype, r.Producer, r.ProducerRef, r.State,
        r.SubjectRef, r.PayloadRef, r.ClaimedBy, Utc(r.CreatedAt), Utc(r.ExpiresAt), r.Decision, r.DecidedBy, Utc(r.DecidedAt),
        r.AuthSource, r.WithdrawnReason, r.ProposalId);

    private static Task<ItemRow?> LockItem(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid itemId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(QueueSql.GetItemForUpdate,
            new { tenant_id = tenantId, item_id = itemId }, tx, cancellationToken: ct));

    private static bool IsExpired(ItemRow r, DateTimeOffset now) =>
        r.State is QueueStates.Open or QueueStates.Claimed && r.ExpiresAt is { } e && Utc(e) <= now;

    private static bool IsWithdrawnExpired(ItemRow r) => r.State == QueueStates.Withdrawn && r.WithdrawnReason == QueueStates.ProposalExpired;

    private static Task InsertItemEvent(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid itemId, string type, string? from, string to, string actor, string? detail, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(QueueSql.InsertEvent, new
        {
            tenant_id = tenantId,
            event_id = Guid.NewGuid(),
            item_id = itemId,
            event_type = type,
            from_state = from,
            to_state = to,
            actor,
            detail,
        }, tx, cancellationToken: ct));

    private static async Task<QueueItemDetail> Reload(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid itemId, CancellationToken ct) =>
        ToDetail((await LockItem(conn, tx, tenantId, itemId, ct))!);

    // D-11: withdraws the item and expires its proposal on the caller's connection and transaction. The caller then RETURNS a
    // result value so the transaction commits. An inconsistent proposal state throws and rolls everything back (T-05-36).
    internal static async Task ExpireInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, QueueItemDetail item, string trigger, CancellationToken ct)
    {
        if (item.ProposalId is not { } proposalId)
        {
            throw new InvalidOperationException("Expiry requires an item with a proposal.");
        }

        if (!ProposalStates.CanTransition(ProposalStates.AwaitingApproval, ProposalStates.Expired))
        {
            throw new InvalidOperationException("Lifecycle table does not permit expiry.");
        }

        var n = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.UpdateState,
            new { tenant_id = tenantId, proposal_id = proposalId, from_state = ProposalStates.AwaitingApproval, to_state = ProposalStates.Expired }, tx, cancellationToken: ct));
        if (n == 1)
        {
            await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
            {
                tenant_id = tenantId,
                event_id = Guid.NewGuid(),
                proposal_id = proposalId,
                event_type = "transition",
                from_state = ProposalStates.AwaitingApproval,
                to_state = ProposalStates.Expired,
                actor = "system",
                detail = "{\"trigger\":\"" + trigger + "\"}",
            }, tx, cancellationToken: ct));
        }
        else
        {
            var current = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "select state from proposal where tenant_id = @tenant_id and proposal_id = @proposal_id",
                new { tenant_id = tenantId, proposal_id = proposalId }, tx, cancellationToken: ct));
            if (current != ProposalStates.Expired)
            {
                throw new InvalidOperationException("Expiry found the proposal in an unexpected state.");
            }
        }

        var w = await conn.ExecuteAsync(new CommandDefinition(QueueSql.Withdraw,
            new { tenant_id = tenantId, item_id = item.ItemId, reason = QueueStates.ProposalExpired }, tx, cancellationToken: ct));
        if (w != 1)
        {
            throw new InvalidOperationException("Withdraw did not affect exactly one row.");
        }

        await InsertItemEvent(conn, tx, tenantId, item.ItemId, QueueStates.EventWithdrawn, item.State, QueueStates.Withdrawn, "system",
            "{\"reason\":\"proposal_expired\",\"trigger\":\"" + trigger + "\"}", ct);
    }

    public Task<ClaimResult> ClaimAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) =>
        uow.ExecuteAsync<ClaimResult>(tenantId, async (conn, tx) =>
        {
            var row = await LockItem(conn, tx, tenantId, itemId, ct);
            if (row is null) { return new ClaimResult.NotFound(); }
            if (IsExpired(row, now))
            {
                await ExpireInTransactionAsync(conn, tx, tenantId, ToDetail(row), "claim", ct);
                return new ClaimResult.ProposalExpired();
            }

            if (IsWithdrawnExpired(row)) { return new ClaimResult.ProposalExpired(); }
            if (row.State == QueueStates.Claimed) { return new ClaimResult.AlreadyClaimed(); }
            if (row.State != QueueStates.Open) { return new ClaimResult.Closed(); }

            var n = await conn.ExecuteAsync(new CommandDefinition(QueueSql.Claim,
                new { tenant_id = tenantId, item_id = itemId, actor, now = now.UtcDateTime }, tx, cancellationToken: ct));
            if (n != 1) { throw new InvalidOperationException("Claim did not affect exactly one row."); }
            await InsertItemEvent(conn, tx, tenantId, itemId, QueueStates.EventClaimed, QueueStates.Open, QueueStates.Claimed, actor, null, ct);
            return new ClaimResult.Claimed(await Reload(conn, tx, tenantId, itemId, ct));
        }, ct);

    public Task<ReleaseResult> ReleaseAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) =>
        uow.ExecuteAsync<ReleaseResult>(tenantId, async (conn, tx) =>
        {
            var row = await LockItem(conn, tx, tenantId, itemId, ct);
            if (row is null) { return new ReleaseResult.NotFound(); }
            if (IsExpired(row, now))
            {
                await ExpireInTransactionAsync(conn, tx, tenantId, ToDetail(row), "release", ct);
                return new ReleaseResult.ProposalExpired();
            }

            if (IsWithdrawnExpired(row)) { return new ReleaseResult.ProposalExpired(); }
            if (row.State == QueueStates.Open) { return new ReleaseResult.NotClaimed(); }
            if (row.State != QueueStates.Claimed) { return new ReleaseResult.Closed(); }
            if (!string.Equals(row.ClaimedBy, actor, StringComparison.Ordinal)) { return new ReleaseResult.NotClaimant(); }

            var n = await conn.ExecuteAsync(new CommandDefinition(QueueSql.Release,
                new { tenant_id = tenantId, item_id = itemId, actor }, tx, cancellationToken: ct));
            if (n != 1) { throw new InvalidOperationException("Release did not affect exactly one row."); }
            await InsertItemEvent(conn, tx, tenantId, itemId, QueueStates.EventReleased, QueueStates.Claimed, QueueStates.Open, actor, null, ct);
            return new ReleaseResult.Released(await Reload(conn, tx, tenantId, itemId, ct));
        }, ct);
}
