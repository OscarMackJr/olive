using System.Security.Claims;
using Olive.Application;
using Olive.Domain;

namespace Olive.Host.Auth;

// D-10: cookie roles are not trusted at decide time. The decision handler asks this service for a fresh ReviewerContext.
public interface IDecisionFreshness
{
    ValueTask<FreshnessOutcome> ResolveAsync(ClaimsPrincipal user, CancellationToken ct);
}

public abstract record FreshnessOutcome
{
    public sealed record Fresh(ReviewerContext Reviewer) : FreshnessOutcome;

    // Reserved for the Entra re-authentication window (plan 05).
    public sealed record Stale : FreshnessOutcome;

    // Fail closed: the identity could not be re-resolved.
    public sealed record Denied : FreshnessOutcome;
}

public sealed class DecisionFreshness(IReviewerIdentitySource identities, IReviewerTenantMap tenantMap, TimeProvider time) : IDecisionFreshness
{
    private readonly TimeProvider _time = time;

    public ValueTask<FreshnessOutcome> ResolveAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        FreshnessOutcome denied = new FreshnessOutcome.Denied();
        // Entra freshness arrives with plan 05; until then any non-stub source fails closed.
        if (user.FindFirstValue(ReviewerClaims.AuthSource) != QueueStates.Stub) { return ValueTask.FromResult(denied); }

        var reviewerId = user.FindFirstValue(ReviewerClaims.ReviewerId);
        var identity = string.IsNullOrEmpty(reviewerId) ? null : identities.Find(reviewerId);
        if (identity is null) { return ValueTask.FromResult(denied); }

        var tenants = tenantMap.TenantsFor(identity.Groups).Distinct().ToArray();
        if (tenants.Length != 1) { return ValueTask.FromResult(denied); }

        FreshnessOutcome fresh = new FreshnessOutcome.Fresh(new ReviewerContext(tenants[0], identity.Oid, identity.Tid, identity.Groups, QueueStates.Stub));
        return ValueTask.FromResult(fresh);
    }
}
