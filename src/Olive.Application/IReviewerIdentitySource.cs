namespace Olive.Application;

// D-07: the dev reviewer identity behind the DevReviewer scheme. Phase 7 replaces the fixture source with Entra OIDC claims.
public sealed record ReviewerIdentity(string ReviewerId, string Oid, string Tid, string Upn, IReadOnlyList<string> Groups);

public interface IReviewerIdentitySource { ReviewerIdentity? Find(string reviewerId); IReadOnlyList<ReviewerIdentity> All(); }
