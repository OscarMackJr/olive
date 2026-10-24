using Dapper;
using Npgsql;
using Olive.Application;
using Olive.Domain;

namespace Olive.Persistence;

// Execution store over Postgres (EXE-01, EXE-02). The per-proposal advisory lock lives on connection A for the whole attempt;
// every write runs on a short transaction on connection B (D-07, D-12).
public sealed class PostgresExecutionStore(TenantUnitOfWork uow, TimeSpan lockTimeout) : IExecutionStore
{
    static PostgresExecutionStore() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    // WR-02: short by default. A lock wait only happens while another attempt holds the lock, and that holder finishes the
    // proposal, so a waiter (the decide request or the sweep) gains nothing by blocking for long.
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    public PostgresExecutionStore(TenantUnitOfWork uow) : this(uow, DefaultLockTimeout) { }

    private sealed class ProposalRow
    {
        public string State { get; set; } = "";
        public string? ActionType { get; set; }
        public string? ParametersJson { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    private sealed class LedgerDbRow
    {
        public Guid ExecutionId { get; set; }
        public string Phase { get; set; } = "";
        public string? Outcome { get; set; }
        public string? Reason { get; set; }
        public string? ResultRef { get; set; }
        public DateTime RecordedAt { get; set; }
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private static LedgerRow ToRow(LedgerDbRow r) => new(r.ExecutionId, r.Phase, r.Outcome, r.Reason, r.ResultRef, Utc(r.RecordedAt));

    public async Task<IExecutionAttempt?> BeginAttemptAsync(Guid tenantId, Guid proposalId, CancellationToken ct)
    {
        var handle = await uow.AcquireAdvisoryLockAsync(tenantId, ExecutionSql.LockExecution,
            new { tenant_id = tenantId, proposal_id = proposalId }, lockTimeout, ct);
        return handle is null ? null : new Attempt(uow, handle, tenantId, proposalId);
    }

    public Task<IReadOnlyList<Guid>> ListApprovedAsync(Guid tenantId, CancellationToken ct) =>
        uow.ExecuteAsync<IReadOnlyList<Guid>>(tenantId, async (conn, tx) =>
            (await conn.QueryAsync<Guid>(new CommandDefinition(ExecutionSql.ListApproved, new { tenant_id = tenantId }, tx, cancellationToken: ct))).ToList(), ct);

    private sealed class Attempt(TenantUnitOfWork uow, IAsyncDisposable handle, Guid tenantId, Guid proposalId) : IExecutionAttempt
    {
        public ValueTask DisposeAsync() => handle.DisposeAsync();

        public Task<ExecutionSnapshot?> LoadAsync(CancellationToken ct) =>
            uow.ExecuteAsync<ExecutionSnapshot?>(tenantId, async (conn, tx) =>
            {
                var p = await conn.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(ExecutionSql.LoadProposal,
                    new { tenant_id = tenantId, proposal_id = proposalId }, tx, cancellationToken: ct));
                if (p is null) { return null; }
                var rows = (await conn.QueryAsync<LedgerDbRow>(new CommandDefinition(ExecutionSql.LoadLedger,
                    new { tenant_id = tenantId, proposal_id = proposalId }, tx, cancellationToken: ct))).ToList();
                var started = rows.FirstOrDefault(r => r.Phase == "started");
                var finished = rows.FirstOrDefault(r => r.Phase == "finished");
                return new ExecutionSnapshot(p.State, p.ActionType ?? "", p.ParametersJson ?? "{}", Utc(p.ExpiresAt),
                    started is null ? null : ToRow(started), finished is null ? null : ToRow(finished));
            }, ct);

        public Task<LedgerRow> RecordStartedAsync(DateTimeOffset now, CancellationToken ct) =>
            uow.ExecuteAsync(tenantId, async (conn, tx) => await InsertLedger(conn, tx, "started", null, null, null, now, ct), ct);

        public Task<LedgerRow> RecordFinishedAsync(FinishedOutcome outcome, string toState, DateTimeOffset now, CancellationToken ct) =>
            uow.ExecuteAsync(tenantId, async (conn, tx) =>
            {
                var row = await InsertLedger(conn, tx, "finished", outcome.Succeeded ? "succeeded" : "failed", outcome.Reason, outcome.ResultRef, now, ct);
                await Transition(conn, tx, toState, outcome.Reason ?? "executed", ct);
                return row;
            }, ct);

        public Task<LedgerRow> RecordRefusalAsync(string reason, string toState, DateTimeOffset now, CancellationToken ct) =>
            uow.ExecuteAsync(tenantId, async (conn, tx) =>
            {
                await InsertLedger(conn, tx, "started", null, null, null, now, ct);
                var row = await InsertLedger(conn, tx, "finished", "failed", reason, null, now, ct);
                await Transition(conn, tx, toState, reason, ct);
                return row;
            }, ct);

        private async Task<LedgerRow> InsertLedger(NpgsqlConnection conn, NpgsqlTransaction tx, string phase, string? outcome, string? reason, string? resultRef, DateTimeOffset now, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            await conn.ExecuteAsync(new CommandDefinition(ExecutionSql.InsertLedger, new
            {
                tenant_id = tenantId,
                execution_id = id,
                proposal_id = proposalId,
                phase,
                outcome,
                reason,
                result_ref = resultRef,
                now = now.UtcDateTime,
            }, tx, cancellationToken: ct));
            return new LedgerRow(id, phase, outcome, reason, resultRef, now.ToUniversalTime());
        }

        // D-07: the state change and its event share the finished row's transaction. A lost compare-and-set rolls everything back.
        private async Task Transition(NpgsqlConnection conn, NpgsqlTransaction tx, string toState, string reason, CancellationToken ct)
        {
            if (!ProposalStates.CanTransition(ProposalStates.Approved, toState))
            {
                throw new InvalidOperationException("Lifecycle table does not permit this execution transition.");
            }

            var n = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.UpdateState,
                new { tenant_id = tenantId, proposal_id = proposalId, from_state = ProposalStates.Approved, to_state = toState }, tx, cancellationToken: ct));
            if (n != 1)
            {
                throw new InvalidOperationException("The proposal was not in the approved state.");
            }

            await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
            {
                tenant_id = tenantId,
                event_id = Guid.NewGuid(),
                proposal_id = proposalId,
                event_type = "transition",
                from_state = ProposalStates.Approved,
                to_state = toState,
                actor = "system",
                detail = "{\"reason\":\"" + reason + "\",\"execution_mode\":\"simulated\"}",
            }, tx, cancellationToken: ct));
        }
    }
}
