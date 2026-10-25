namespace Olive.Domain;

// Literals must equal the CHECK values in sql/migrations/0008_review_queue.sql.
public static class QueueStates
{
    public const string Open = "open";
    public const string Claimed = "claimed";
    public const string Decided = "decided";
    public const string Withdrawn = "withdrawn";

    public const string ActionApproval = "action_approval";
    public const string IdentityReview = "identity_review";
    public const string ProposalSubtype = "proposal";
    public const string Olive = "olive";

    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Accept = "accept";
    public const string Defer = "defer";

    public const string EventCreated = "created";
    public const string EventClaimed = "claimed";
    public const string EventReleased = "released";
    public const string EventDecided = "decided";
    public const string EventDeferred = "deferred";
    public const string EventWithdrawn = "withdrawn";

    public const string ProposalExpired = "proposal_expired";

    public const string Entra = "entra";
    public const string Stub = "stub";
}
