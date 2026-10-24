using System.Globalization;
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

    // The Entra session is outside the freshness window: the page re-challenges with prompt=login.
    public sealed record Stale : FreshnessOutcome;

    // Fail closed: the identity could not be re-resolved.
    public sealed record Denied : FreshnessOutcome;
}

// The configured Entra window (OLIVE_OIDC_FRESHNESS_MINUTES); it can only narrow the code maximum.
public sealed record DecisionFreshnessOptions(TimeSpan? Configured);

public sealed class DecisionFreshness(IReviewerIdentitySource identities, IReviewerTenantMap tenantMap, TimeProvider time, DecisionFreshnessOptions? options = null) : IDecisionFreshness
{
    public const int MaxFreshnessMinutes = 5;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _window = TimeSpan.FromMinutes(Math.Min(options?.Configured?.TotalMinutes ?? MaxFreshnessMinutes, MaxFreshnessMinutes));

    public ValueTask<FreshnessOutcome> ResolveAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        FreshnessOutcome denied = new FreshnessOutcome.Denied();
        var source = user.FindFirstValue(ReviewerClaims.AuthSource);
        if (source == QueueStates.Entra) { return ValueTask.FromResult(ResolveEntra(user)); }
        if (source != QueueStates.Stub) { return ValueTask.FromResult(denied); }

        var reviewerId = user.FindFirstValue(ReviewerClaims.ReviewerId);
        var identity = string.IsNullOrEmpty(reviewerId) ? null : identities.Find(reviewerId);
        if (identity is null) { return ValueTask.FromResult(denied); }

        var tenants = tenantMap.TenantsFor(identity.Groups).Distinct().ToArray();
        if (tenants.Length != 1) { return ValueTask.FromResult(denied); }

        FreshnessOutcome fresh = new FreshnessOutcome.Fresh(new ReviewerContext(tenants[0], identity.Oid, identity.Tid, identity.Groups, QueueStates.Stub));
        return ValueTask.FromResult(fresh);
    }

    // D-05, D-06: the session must carry an auth_time inside the window. Missing, zero, unparseable or future-dated evidence is stale.
    private FreshnessOutcome ResolveEntra(ClaimsPrincipal user)
    {
        var reviewer = user.ToReviewerContext();
        if (reviewer is null) { return new FreshnessOutcome.Denied(); }

        if (!long.TryParse(user.FindFirstValue(ReviewerClaims.AuthTime), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            return new FreshnessOutcome.Stale();
        }

        var now = time.GetUtcNow();
        var authTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (authTime > now + Skew) { return new FreshnessOutcome.Stale(); }
        return now - authTime <= _window ? new FreshnessOutcome.Fresh(reviewer) : new FreshnessOutcome.Stale();
    }
}
