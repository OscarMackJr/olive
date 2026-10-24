using System.Security.Claims;
using Olive.Application;
using Olive.Catalog;
using Olive.Domain;
using Olive.Host.Auth;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class DecisionFreshnessTests
{
    private sealed class MutableSource : IReviewerIdentitySource
    {
        public Dictionary<string, ReviewerIdentity> Items { get; } = new(StringComparer.Ordinal);

        public ReviewerIdentity? Find(string reviewerId) => Items.GetValueOrDefault(reviewerId);

        public IReadOnlyList<ReviewerIdentity> All() => Items.Values.ToArray();
    }

    private static ReviewerIdentity Identity(params string[] groups) => new("rev-1", "oid-1", "tid-1", "rev1@example.test", groups);

    private static ClaimsPrincipal Cookie(string authSource, string? reviewerId) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ReviewerClaims.AuthSource, authSource) }
                .Concat(reviewerId is null ? [] : [new Claim(ReviewerClaims.ReviewerId, reviewerId)]),
            ReviewerCookie.Scheme));

    private static DecisionFreshness Service(MutableSource source) => new(source, ReviewerTenantMap.LoadEmbedded(), TimeProvider.System);

    [Fact]
    public async Task APR_03_fixture_with_role_resolves_fresh_with_the_role_in_groups()
    {
        var source = new MutableSource();
        source.Items["rev-1"] = Identity("tenant-alpha-reviewers", "role:case_supervisor");
        var outcome = await Service(source).ValueTaskAsync(Cookie(QueueStates.Stub, "rev-1"));
        var fresh = Assert.IsType<FreshnessOutcome.Fresh>(outcome);
        Assert.Contains("role:case_supervisor", fresh.Reviewer.Groups);
        Assert.Equal(QueueStates.Stub, fresh.Reviewer.AuthSource);
        Assert.Equal("oid-1", fresh.Reviewer.Oid);
    }

    [Fact]
    public async Task APR_03_role_removed_after_cookie_issue_is_reflected_at_decide_time()
    {
        var source = new MutableSource();
        source.Items["rev-1"] = Identity("tenant-alpha-reviewers", "role:case_supervisor");
        var service = Service(source);
        var cookie = Cookie(QueueStates.Stub, "rev-1");
        Assert.IsType<FreshnessOutcome.Fresh>(await service.ValueTaskAsync(cookie));

        source.Items["rev-1"] = Identity("tenant-alpha-reviewers");
        var fresh = Assert.IsType<FreshnessOutcome.Fresh>(await service.ValueTaskAsync(cookie));
        Assert.DoesNotContain("role:case_supervisor", fresh.Reviewer.Groups);
    }

    [Fact]
    public async Task APR_03_unknown_reviewer_id_is_denied()
    {
        var outcome = await Service(new MutableSource()).ValueTaskAsync(Cookie(QueueStates.Stub, "nobody"));
        Assert.IsType<FreshnessOutcome.Denied>(outcome);
        Assert.IsType<FreshnessOutcome.Denied>(await Service(new MutableSource()).ValueTaskAsync(Cookie(QueueStates.Stub, null)));
    }

    [Fact]
    public async Task APR_03_entra_auth_source_is_denied_until_the_entra_freshness_path_exists()
    {
        var source = new MutableSource();
        source.Items["rev-1"] = Identity("tenant-alpha-reviewers", "role:case_supervisor");
        Assert.IsType<FreshnessOutcome.Denied>(await Service(source).ValueTaskAsync(Cookie("entra", "rev-1")));
    }

    [Fact]
    public async Task APR_03_two_tenant_reviewer_is_denied()
    {
        var source = new MutableSource();
        source.Items["rev-1"] = Identity("tenant-alpha-reviewers", "tenant-beta-reviewers", "role:case_supervisor");
        Assert.IsType<FreshnessOutcome.Denied>(await Service(source).ValueTaskAsync(Cookie(QueueStates.Stub, "rev-1")));
    }
}

internal static class DecisionFreshnessTestExtensions
{
    public static async Task<FreshnessOutcome> ValueTaskAsync(this IDecisionFreshness service, ClaimsPrincipal user) =>
        await service.ResolveAsync(user, TestContext.Current.CancellationToken);
}
