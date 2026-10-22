namespace Olive.Domain;

public static class TraceRefusalReasons
{
    public const string Unresolved = "trace_unresolved";
    public const string TenantMismatch = "trace_tenant_mismatch";
    public const string AgentMismatch = "trace_agent_mismatch";
    public const string ProposalMismatch = "trace_proposal_mismatch";
}
