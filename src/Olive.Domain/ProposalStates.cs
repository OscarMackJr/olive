namespace Olive.Domain;

// Matches vocabulary action_outcomes in order. Phase 1 writes only Proposed (D-06).
public static class ProposalStates
{
    public const string Proposed = "proposed";
    public const string RefusedPolicy = "refused_policy";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Executed = "executed";
    public const string ExecutionFailed = "execution_failed";
    public const string Expired = "expired";

    public static readonly IReadOnlyList<string> All =
        [Proposed, RefusedPolicy, AwaitingApproval, Approved, Rejected, Executed, ExecutionFailed, Expired];
}
