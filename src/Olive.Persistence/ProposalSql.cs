namespace Olive.Persistence;

// Every statement keeps the application tenant predicate even though forced RLS also applies (AGENTS.md).
public static class ProposalSql
{
    public const string Insert =
        "insert into proposal (tenant_id, proposal_id, agent_id, key_id, on_behalf_of, purpose, key_expires_at, nono_session_id, run_id, action_type, party_ref, parameters, justification, trace_ref, canonical_body, body_sha256, proposal_hash, trace_verification, trace_schema_version, expires_at) " +
        "values (@tenant_id, @proposal_id, @agent_id, @key_id, @on_behalf_of, @purpose, @key_expires_at, @nono_session_id, @run_id, @action_type, @party_ref, @parameters::jsonb, @justification, @trace_ref, @canonical_body, @body_sha256, @proposal_hash, @trace_verification, @trace_schema_version, @expires_at) " +
        "on conflict on constraint proposal_run_unique do nothing returning proposal_id";

    public const string InsertEvent =
        "insert into proposal_event (tenant_id, event_id, proposal_id, event_type, from_state, to_state, actor, detail) " +
        "values (@tenant_id, @event_id, @proposal_id, @event_type, @from_state, @to_state, @actor, @detail::jsonb)";

    // D-08 / WR-01: the rate scope key. The tenant uuid text is fixed length and the agent id is length-prefixed, so no two distinct
    // (tenant, agent, action) scopes encode to the same string, whatever characters the agent id or action type contain. The 64-bit
    // hash can still collide; a collision only serialises two unrelated scopes and never skips a lock, so it is harmless.
    public const string RateLockKey =
        "hashtextextended(@tenant_id::text || ':' || length(@agent_id)::text || ':' || @agent_id || ':' || @action_type, 0)";

    // Held to the end of the transaction. Taken only after catalog, purpose and entitlement passed.
    public const string LockRate = "select pg_advisory_xact_lock(" + RateLockKey + ")";

    // D-09/D-10: refused proposals never count; created_at is database time so the window uses now() in SQL.
    public const string CountAccepted =
        "select count(*) from proposal where tenant_id = @tenant_id and agent_id = @agent_id and action_type = @action_type " +
        "and state <> 'refused_policy' and created_at > now() - make_interval(secs => @window_seconds)";

    public const string Get =
        "select proposal_id, tenant_id, state, agent_id, key_id, on_behalf_of, purpose, key_expires_at, nono_session_id, run_id, action_type, party_ref, parameters::text as parameters_json, justification, trace_ref, created_at, refusal_reason, trace_verification, trace_schema_version, expires_at " +
        "from proposal where tenant_id = @tenant_id and proposal_id = @proposal_id";

    public const string FindByRun =
        "select proposal_id, state, body_sha256, on_behalf_of, purpose, refusal_reason from proposal where tenant_id = @tenant_id and agent_id = @agent_id and run_id = @run_id";

    public const string UpdateState =
        "update proposal set state = @to_state where tenant_id = @tenant_id and proposal_id = @proposal_id and state = @from_state";

    public const string RefuseWithReason =
        "update proposal set state = 'refused_policy', refusal_reason = @reason where tenant_id = @tenant_id and proposal_id = @proposal_id and state = 'proposed'";
}
