namespace Olive.Persistence;

// Read-only. Every statement keeps the application tenant predicate even though forced RLS also applies.
public static class AuditSql
{
    public const string Proposal =
        "select proposal_id, action_type, party_ref, parameters::text as parameters_json, justification, body_sha256, proposal_hash, trace_verification " +
        "from proposal where tenant_id = @tenant_id and proposal_id = @proposal_id";

    public const string Chain =
        "select policy_decision::text as policy_decision_json, decided_by, auth_source, decided_at, rationale, execution_mode, started_at, outcome, reason, finished_at " +
        "from execution_chain where tenant_id = @tenant_id and proposal_id = @proposal_id";
}
