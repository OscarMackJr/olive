namespace Olive.Domain;

// Matches vocabulary action_outcomes in order. Accepted proposals reach AwaitingApproval (Phase 4).
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

    // POL-06: the full lifecycle. Terminal states (refused_policy, rejected, executed, execution_failed, expired) have no edges.
    private static readonly Dictionary<string, HashSet<string>> Edges = new(StringComparer.Ordinal)
    {
        [Proposed] = new(StringComparer.Ordinal) { RefusedPolicy, AwaitingApproval },
        [AwaitingApproval] = new(StringComparer.Ordinal) { Approved, Rejected, Expired },
        [Approved] = new(StringComparer.Ordinal) { Executed, ExecutionFailed, Expired },
    };

    public static bool CanTransition(string from, string to) =>
        from is not null && to is not null && Edges.TryGetValue(from, out var next) && next.Contains(to);
}
