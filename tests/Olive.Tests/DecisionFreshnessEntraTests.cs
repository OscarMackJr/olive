using System.Security.Claims;
using Olive.Application;
using Olive.Catalog;
using Olive.Domain;
using Olive.Host.Auth;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class DecisionFreshnessEntraTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class NoSource : IReviewerIdentitySource
    {
        public ReviewerIdentity? Find(string reviewerId) => null;

        public IReadOnlyList<ReviewerIdentity> All() => [];
    }

    private static DecisionFreshness Service(int? configuredMinutes = null) =>
        new(new NoSource(), ReviewerTenantMap.LoadEmbedded(), new ManualTimeProvider(Now),
            configuredMinutes is { } m ? new DecisionFreshnessOptions(TimeSpan.FromMinutes(m)) : null);

    private static ClaimsPrincipal Cookie(TimeSpan? authAge, bool withTenant = true, string authSource = QueueStates.Entra)
    {
        var claims = new List<Claim>
        {
            new(ReviewerClaims.AuthSource, authSource),
            new(ReviewerClaims.Oid, "oid-1"),
            new(ReviewerClaims.Tid, "tid-1"),
            new(ReviewerClaims.Group, "role:case_supervisor"),
        };
        if (withTenant) { claims.Add(new Claim(ReviewerClaims.TenantId, TestPaths.TenantAlpha.ToString("D"))); }
        if (authAge is { } age) { claims.Add(new Claim(ReviewerClaims.AuthTime, (Now - age).ToUnixTimeSeconds().ToString())); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, ReviewerCookie.Scheme));
    }

    [Fact]
    public async Task APR_03_entra_auth_time_four_minutes_old_is_fresh_with_cookie_claims()
    {
        var fresh = Assert.IsType<FreshnessOutcome.Fresh>(await Service().ValueTaskAsync(Cookie(TimeSpan.FromMinutes(4))));
        Assert.Equal("oid-1", fresh.Reviewer.Oid);
        Assert.Contains("role:case_supervisor", fresh.Reviewer.Groups);
        Assert.Equal(QueueStates.Entra, fresh.Reviewer.AuthSource);
    }

    [Fact]
    public async Task APR_03_entra_auth_time_six_minutes_old_is_stale()
    {
        Assert.IsType<FreshnessOutcome.Stale>(await Service().ValueTaskAsync(Cookie(TimeSpan.FromMinutes(6))));
    }

    [Fact]
    public async Task APR_03_entra_missing_auth_time_is_stale()
    {
        Assert.IsType<FreshnessOutcome.Stale>(await Service().ValueTaskAsync(Cookie(null)));
    }

    [Fact]
    public async Task APR_03_configured_two_minutes_is_honoured()
    {
        var service = Service(2);
        Assert.IsType<FreshnessOutcome.Fresh>(await service.ValueTaskAsync(Cookie(TimeSpan.FromMinutes(1))));
        Assert.IsType<FreshnessOutcome.Stale>(await service.ValueTaskAsync(Cookie(TimeSpan.FromMinutes(3))));
    }

    [Fact]
    public async Task APR_03_configured_thirty_minutes_clamps_to_five()
    {
        Assert.Equal(5, DecisionFreshness.MaxFreshnessMinutes);
        var service = Service(30);
        Assert.IsType<FreshnessOutcome.Fresh>(await service.ValueTaskAsync(Cookie(TimeSpan.FromMinutes(4))));
        Assert.IsType<FreshnessOutcome.Stale>(await service.ValueTaskAsync(Cookie(TimeSpan.FromMinutes(6))));
    }

    [Fact]
    public async Task APR_03_future_dated_auth_time_ten_minutes_ahead_is_stale()
    {
        Assert.IsType<FreshnessOutcome.Stale>(await Service().ValueTaskAsync(Cookie(TimeSpan.FromMinutes(-10))));
    }

    [Fact]
    public async Task APR_03_auth_time_thirty_seconds_ahead_is_within_skew_and_fresh()
    {
        Assert.IsType<FreshnessOutcome.Fresh>(await Service().ValueTaskAsync(Cookie(TimeSpan.FromSeconds(-30))));
    }

    [Fact]
    public async Task APR_03_entra_cookie_without_tenant_is_denied()
    {
        Assert.IsType<FreshnessOutcome.Denied>(await Service().ValueTaskAsync(Cookie(TimeSpan.FromMinutes(1), withTenant: false)));
    }

    [Fact]
    public async Task APR_03_unknown_auth_source_is_denied_and_stub_path_is_unchanged()
    {
        Assert.IsType<FreshnessOutcome.Denied>(await Service().ValueTaskAsync(Cookie(TimeSpan.FromMinutes(1), authSource: "other")));
        Assert.IsType<FreshnessOutcome.Denied>(await Service().ValueTaskAsync(Cookie(TimeSpan.FromMinutes(1), authSource: QueueStates.Stub)));
    }
}
