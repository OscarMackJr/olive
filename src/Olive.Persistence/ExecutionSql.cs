namespace Olive.Persistence;

// Every statement keeps the application tenant predicate even though forced RLS also applies. Time comes from the caller, never now().
public static class ExecutionSql
{
    // D-02: one lock per (tenant, proposal), held on a dedicated connection for the whole attempt.
    public const string LockExecution =
        "select pg_advisory_xact_lock(hashtextextended('exec:' || @tenant_id::text || ':' || @proposal_id::text, 0))";

    public const string LoadProposal =
        "select state, action_type, parameters::text as parameters_json, expires_at from proposal where tenant_id = @tenant_id and proposal_id = @proposal_id";

    public const string LoadLedger =
        "select execution_id, phase, outcome, reason, result_ref, recorded_at from execution_ledger where tenant_id = @tenant_id and proposal_id = @proposal_id order by recorded_at, phase desc";

    public const string InsertLedger =
        "insert into execution_ledger (tenant_id, execution_id, proposal_id, phase, execution_mode, outcome, reason, result_ref, recorded_at) " +
        "values (@tenant_id, @execution_id, @proposal_id, @phase, 'simulated', @outcome, @reason, @result_ref, @now)";

    public const string ListApproved =
        "select proposal_id from proposal where tenant_id = @tenant_id and state = 'approved' order by created_at";
}
