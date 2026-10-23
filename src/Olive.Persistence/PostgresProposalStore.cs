using Dapper;
using Olive.Application;

namespace Olive.Persistence;

public sealed class PostgresProposalStore(TenantUnitOfWork uow) : IProposalStore
{
    static PostgresProposalStore() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private sealed class Row
    {
        public Guid ProposalId { get; set; }
        public Guid TenantId { get; set; }
        public string State { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string OnBehalfOf { get; set; } = "";
        public string Purpose { get; set; } = "";
        public DateTime KeyExpiresAt { get; set; }
        public string? NonoSessionId { get; set; }
        public string RunId { get; set; } = "";
        public string? ActionType { get; set; }
        public string? PartyRef { get; set; }
        public string? ParametersJson { get; set; }
        public string? Justification { get; set; }
        public string? TraceRef { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? RefusalReason { get; set; }
        public string? TraceVerification { get; set; }
        public string? TraceSchemaVersion { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));

    public Task<Guid?> InsertAsync(Guid tenantId, NewProposal p, CancellationToken ct) => InsertCoreAsync(tenantId, p, null, null, ct);

    // olive_app has no INSERT grant on state (migration 0003), so a refusal is insert then compare-and-set, atomically (D-05).
    public Task<Guid?> InsertRefusedAsync(Guid tenantId, NewProposal p, string refusalReason, CancellationToken ct, string? policyDetailJson = null) =>
        InsertCoreAsync(tenantId, p, refusalReason, policyDetailJson, ct);

    private Task<Guid?> InsertCoreAsync(Guid tenantId, NewProposal p, string? refusalReason, string? policyDetailJson, CancellationToken ct) =>
        uow.ExecuteAsync<Guid?>(tenantId, async (conn, tx) =>
        {
            var id = Guid.NewGuid();
            var inserted = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(ProposalSql.Insert, new
            {
                tenant_id = tenantId,
                proposal_id = id,
                agent_id = p.AgentId,
                key_id = p.KeyId,
                on_behalf_of = p.OnBehalfOf,
                purpose = p.Purpose,
                key_expires_at = p.KeyExpiresAt.UtcDateTime,
                nono_session_id = p.NonoSessionId,
                run_id = p.RunId,
                action_type = p.ActionType,
                party_ref = p.PartyRef,
                parameters = p.ParametersJson,
                justification = p.Justification,
                trace_ref = p.TraceRef,
                canonical_body = p.CanonicalBody,
                body_sha256 = p.BodySha256,
                proposal_hash = p.ProposalHash,
                trace_verification = p.TraceVerification,
                trace_schema_version = p.TraceSchemaVersion,
                expires_at = p.ExpiresAt?.UtcDateTime,
            }, tx, cancellationToken: ct));
            if (inserted is null)
            {
                return null;
            }

            await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
            {
                tenant_id = tenantId,
                event_id = Guid.NewGuid(),
                proposal_id = id,
                event_type = "submitted",
                from_state = (string?)null,
                to_state = "proposed",
                actor = p.AgentId,
                detail = (string?)null,
            }, tx, cancellationToken: ct));

            if (refusalReason is not null && policyDetailJson is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
                {
                    tenant_id = tenantId,
                    event_id = Guid.NewGuid(),
                    proposal_id = id,
                    event_type = "policy_evaluated",
                    from_state = (string?)null,
                    to_state = "proposed",
                    actor = p.AgentId,
                    detail = policyDetailJson,
                }, tx, cancellationToken: ct));
            }

            if (refusalReason is not null)
            {
                var n = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.RefuseWithReason,
                    new { tenant_id = tenantId, proposal_id = id, reason = refusalReason }, tx, cancellationToken: ct));
                if (n != 1)
                {
                    throw new InvalidOperationException("Refusal compare-and-set did not affect exactly one row.");
                }

                await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
                {
                    tenant_id = tenantId,
                    event_id = Guid.NewGuid(),
                    proposal_id = id,
                    event_type = policyDetailJson is null ? "trace_refused" : "policy_refused",
                    from_state = "proposed",
                    to_state = "refused_policy",
                    actor = p.AgentId,
                    detail = (string?)null,
                }, tx, cancellationToken: ct));
            }

            return id;
        }, ct);

    // D-08..D-12, D-20: lock, count (before insert), insert, then accept-by-CAS or rate-refuse, all in one transaction.
    public Task<EvaluatedInsertResult?> InsertEvaluatedAsync(Guid tenantId, NewProposal p, int rateLimit, int windowSeconds, Olive.Domain.PolicyVerdict priorPassing, CancellationToken ct) =>
        uow.ExecuteAsync<EvaluatedInsertResult?>(tenantId, async (conn, tx) =>
        {
            if (p.ActionType is null)
            {
                throw new ArgumentException("POL-03: an action type is required for the rate scope.", nameof(p));
            }

            await conn.ExecuteScalarAsync(new CommandDefinition(ProposalSql.LockRate,
                new { tenant_id = tenantId, agent_id = p.AgentId, action_type = p.ActionType }, tx, cancellationToken: ct));
            var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(ProposalSql.CountAccepted,
                new { tenant_id = tenantId, agent_id = p.AgentId, action_type = p.ActionType, window_seconds = (double)windowSeconds }, tx, cancellationToken: ct));
            var within = count < rateLimit;
            var verdict = Olive.Domain.PolicyEvaluator.AppendRate(priorPassing, within);

            // Refused rows carry no expiry: decide before inserting, never insert then update.
            var row = within ? p : p with { ExpiresAt = null };
            var id = Guid.NewGuid();
            var inserted = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(ProposalSql.Insert, new
            {
                tenant_id = tenantId,
                proposal_id = id,
                agent_id = row.AgentId,
                key_id = row.KeyId,
                on_behalf_of = row.OnBehalfOf,
                purpose = row.Purpose,
                key_expires_at = row.KeyExpiresAt.UtcDateTime,
                nono_session_id = row.NonoSessionId,
                run_id = row.RunId,
                action_type = row.ActionType,
                party_ref = row.PartyRef,
                parameters = row.ParametersJson,
                justification = row.Justification,
                trace_ref = row.TraceRef,
                canonical_body = row.CanonicalBody,
                body_sha256 = row.BodySha256,
                proposal_hash = row.ProposalHash,
                trace_verification = row.TraceVerification,
                trace_schema_version = row.TraceSchemaVersion,
                expires_at = row.ExpiresAt?.UtcDateTime,
            }, tx, cancellationToken: ct));
            if (inserted is null)
            {
                return null;
            }

            Task Event(string type, string? from, string to, string? detail) => conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
            {
                tenant_id = tenantId,
                event_id = Guid.NewGuid(),
                proposal_id = id,
                event_type = type,
                from_state = from,
                to_state = to,
                actor = p.AgentId,
                detail,
            }, tx, cancellationToken: ct));

            await Event("submitted", null, "proposed", null);
            await Event("policy_evaluated", null, "proposed", verdict.ToDetailJson());

            if (!within)
            {
                var n = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.RefuseWithReason,
                    new { tenant_id = tenantId, proposal_id = id, reason = verdict.Reason }, tx, cancellationToken: ct));
                if (n != 1)
                {
                    throw new InvalidOperationException("Refusal compare-and-set did not affect exactly one row.");
                }

                await Event("policy_refused", "proposed", "refused_policy", null);
                return new EvaluatedInsertResult(id, Olive.Domain.ProposalStates.RefusedPolicy, verdict.Reason);
            }

            var accepted = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.UpdateState,
                new { tenant_id = tenantId, proposal_id = id, from_state = Olive.Domain.ProposalStates.Proposed, to_state = Olive.Domain.ProposalStates.AwaitingApproval }, tx, cancellationToken: ct));
            if (accepted != 1)
            {
                throw new InvalidOperationException("Accept compare-and-set did not affect exactly one row.");
            }

            await Event("transition", "proposed", "awaiting_approval", null);
            return new EvaluatedInsertResult(id, Olive.Domain.ProposalStates.AwaitingApproval, null);
        }, ct);

    public Task<ProposalRecord?> GetAsync(Guid tenantId, Guid proposalId, CancellationToken ct) =>
        uow.ExecuteAsync<ProposalRecord?>(tenantId, async (conn, tx) =>
        {
            var r = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(ProposalSql.Get,
                new { tenant_id = tenantId, proposal_id = proposalId }, tx, cancellationToken: ct));
            return r is null ? null : new ProposalRecord(r.ProposalId, r.TenantId, r.State, r.AgentId, r.KeyId, r.OnBehalfOf, r.Purpose,
                Utc(r.KeyExpiresAt), r.NonoSessionId, r.RunId, r.ActionType, r.PartyRef, r.ParametersJson, r.Justification, r.TraceRef, Utc(r.CreatedAt),
                r.RefusalReason, r.TraceVerification, r.TraceSchemaVersion, r.ExpiresAt is { } e ? Utc(e) : null);
        }, ct);

    private sealed class RunRow
    {
        public Guid ProposalId { get; set; }
        public string State { get; set; } = "";
        public string? BodySha256 { get; set; }
        public string OnBehalfOf { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string? RefusalReason { get; set; }
    }

    public Task<ProposalRunRecord?> FindByRunAsync(Guid tenantId, string agentId, string runId, CancellationToken ct) =>
        uow.ExecuteAsync<ProposalRunRecord?>(tenantId, async (conn, tx) =>
        {
            var r = await conn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(ProposalSql.FindByRun,
                new { tenant_id = tenantId, agent_id = agentId, run_id = runId }, tx, cancellationToken: ct));
            return r is null ? null : new ProposalRunRecord(r.ProposalId, r.State, r.BodySha256, r.OnBehalfOf, r.Purpose, r.RefusalReason);
        }, ct);

    public Task<bool> TransitionAsync(Guid tenantId, Guid proposalId, string fromState, string toState, string actor, CancellationToken ct)
    {
        // WR-07: 0006 requires refused_policy and refusal_reason to agree. A plain state CAS cannot set or clear the reason, so it
        // would only ever fail with SQLSTATE 23514 (a 500). Refusals are written only by InsertRefusedAsync (D-05), and
        // refused_policy is terminal; reject both directions here, before any database work.
        if (string.Equals(toState, "refused_policy", StringComparison.Ordinal) || string.Equals(fromState, "refused_policy", StringComparison.Ordinal))
        {
            throw new ArgumentException("REQ TRC-03 / D-05: refused_policy is written only by InsertRefusedAsync and is terminal.", nameof(toState));
        }

        // POL-06: illegal edges are rejected before any database work.
        if (!Olive.Domain.ProposalStates.CanTransition(fromState, toState))
        {
            throw new ArgumentException("POL-06: transition is not permitted by the lifecycle table.", nameof(toState));
        }

        return uow.ExecuteAsync(tenantId, async (conn, tx) =>
        {
            var n = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.UpdateState,
                new { tenant_id = tenantId, proposal_id = proposalId, from_state = fromState, to_state = toState }, tx, cancellationToken: ct));
            if (n == 0)
            {
                return false;
            }

            await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
            {
                tenant_id = tenantId,
                event_id = Guid.NewGuid(),
                proposal_id = proposalId,
                event_type = "transition",
                from_state = fromState,
                to_state = toState,
                actor,
                detail = (string?)null,
            }, tx, cancellationToken: ct));
            return true;
        }, ct);
    }
}
