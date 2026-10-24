using Dapper;
using Olive.Application;

namespace Olive.Persistence;

// UI-05: one tenant transaction, two reads, no writes.
public sealed class PostgresAuditStore(TenantUnitOfWork uow) : IAuditStore
{
    static PostgresAuditStore() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private sealed class ProposalRow
    {
        public Guid ProposalId { get; set; }
        public string ActionType { get; set; } = "";
        public string PartyRef { get; set; } = "";
        public string ParametersJson { get; set; } = "{}";
        public string Justification { get; set; } = "";
        public string BodySha256 { get; set; } = "";
        public string ProposalHash { get; set; } = "";
        public string TraceVerification { get; set; } = "";
    }

    private sealed class ChainDbRow
    {
        public string? PolicyDecisionJson { get; set; }
        public string? DecidedBy { get; set; }
        public string? AuthSource { get; set; }
        public DateTime? DecidedAt { get; set; }
        public string? Rationale { get; set; }
        public string ExecutionMode { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public string? Outcome { get; set; }
        public string? Reason { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    private static DateTimeOffset? UtcOrNull(DateTime? d) => d is { } v ? Utc(v) : null;

    public Task<AuditRecord?> GetAsync(Guid tenantId, Guid proposalId, CancellationToken ct) =>
        uow.ExecuteAsync<AuditRecord?>(tenantId, async (conn, tx) =>
        {
            var args = new { tenant_id = tenantId, proposal_id = proposalId };
            var p = await conn.QuerySingleOrDefaultAsync<ProposalRow>(new CommandDefinition(AuditSql.Proposal, args, tx, cancellationToken: ct));
            if (p is null) { return null; }
            var c = await conn.QuerySingleOrDefaultAsync<ChainDbRow>(new CommandDefinition(AuditSql.Chain, args, tx, cancellationToken: ct));
            var chain = c is null ? null : new ChainRow(c.PolicyDecisionJson, c.DecidedBy, c.AuthSource, UtcOrNull(c.DecidedAt), c.Rationale, c.ExecutionMode, Utc(c.StartedAt), c.Outcome, c.Reason, UtcOrNull(c.FinishedAt));
            return new AuditRecord(p.ProposalId, p.ActionType, p.PartyRef, p.ParametersJson, p.Justification, p.BodySha256, p.ProposalHash, p.TraceVerification, chain);
        }, ct);
}
