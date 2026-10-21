namespace Olive.Persistence;

// Every statement keeps the application tenant predicate even though forced RLS also applies (AGENTS.md).
public static class ProposalSql
{
    public const string Insert =
        "insert into proposal (tenant_id, proposal_id, state, agent_id, key_id, on_behalf_of, purpose, key_expires_at, nono_session_id, run_id, action_type, party_ref, parameters, justification, trace_ref) " +
        "values (@tenant_id, @proposal_id, 'proposed', @agent_id, @key_id, @on_behalf_of, @purpose, @key_expires_at, @nono_session_id, @run_id, @action_type, @party_ref, @parameters::jsonb, @justification, @trace_ref) " +
        "on conflict on constraint proposal_run_unique do nothing returning proposal_id";

    public const string InsertEvent =
        "insert into proposal_event (tenant_id, event_id, proposal_id, event_type, from_state, to_state, actor) " +
        "values (@tenant_id, @event_id, @proposal_id, @event_type, @from_state, @to_state, @actor)";

    public const string Get =
        "select proposal_id, tenant_id, state, agent_id, key_id, on_behalf_of, purpose, key_expires_at, nono_session_id, run_id, action_type, party_ref, parameters::text as parameters_json, justification, trace_ref, created_at " +
        "from proposal where tenant_id = @tenant_id and proposal_id = @proposal_id";

    public const string UpdateState =
        "update proposal set state = @to_state where tenant_id = @tenant_id and proposal_id = @proposal_id and state = @from_state";
}
