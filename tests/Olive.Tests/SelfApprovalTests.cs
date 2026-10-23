using System.Net;
using Olive.Application;
using Olive.Integrations.Entra;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class SelfApprovalTests(PostgresFixture fixture)
{
    private static OliveApiFactory Factory(TestDatabase db, ResolverFault fault = ResolverFault.None, string? timeoutMs = null) =>
        new(db, innerUserIdentity: new FaultingUserIdentityResolver(FixtureUserIdentityResolver.CreateDefault(), fault),
            extraConfig: timeoutMs is null ? null : new Dictionary<string, string?> { ["OLIVE_IDENTITY_RESOLVER_TIMEOUT_MS"] = timeoutMs });

    [Fact]
    public async Task APR_02_requester_approving_their_own_proposal_is_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "sa1");
        await DecisionHttp.ClaimAsync(factory, "alpha-self", item);

        var response = await DecisionHttp.Decide(factory, "alpha-self", item, "approve");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("self_approval_forbidden", await DecisionHttp.ReasonOf(response));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task APR_02_upn_lookalike_with_a_different_oid_is_allowed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "sa2");
        await DecisionHttp.ClaimAsync(factory, "alpha-lookalike", item);

        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-lookalike", item, "approve")).StatusCode);
        Assert.Equal("approved", await DecisionHttp.ProposalState(db, proposal));
    }

    [Theory]
    [InlineData("ambiguous.alpha@example.test")]
    [InlineData("nobody.registered@example.test")]
    public async Task APR_02_ambiguous_or_unregistered_requester_is_refused_as_unresolved(string onBehalfOf)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "sa3", onBehalfOf);
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        var response = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("approval_identity_unresolved", await DecisionHttp.ReasonOf(response));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
    }

    [Theory]
    [InlineData(ResolverFault.Throw, null)]
    [InlineData(ResolverFault.Unavailable, null)]
    [InlineData(ResolverFault.Hang, "300")]
    public async Task APR_02_resolver_outage_on_approve_is_503_and_the_item_stays_claimed(ResolverFault fault, string? timeout)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, fault, timeout);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "sa4");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        var response = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("identity_resolution_unavailable", await DecisionHttp.ReasonOf(response));
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
        Assert.DoesNotContain("identity-fault-secret-detail", factory.Logs.Text);
    }

    [Theory]
    [InlineData(ResolverFault.Throw)]
    [InlineData(ResolverFault.Unavailable)]
    [InlineData(ResolverFault.Malformed)]
    public async Task D_18_reject_succeeds_during_a_resolver_outage_and_never_calls_the_resolver(ResolverFault fault)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, fault);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "sa5");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        var response = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "reject");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("rejected", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(0, factory.UserIdentity.CallCount);
    }

    [Fact]
    public async Task D_18_requester_may_reject_their_own_proposal_without_a_lookup()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "sa6");
        await DecisionHttp.ClaimAsync(factory, "alpha-self", item);

        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-self", item, "reject")).StatusCode);
        Assert.Equal("rejected", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(0, factory.UserIdentity.CallCount);
    }

    [Fact]
    public async Task D_18_a_reject_still_needs_the_claim_and_a_rationale()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, ResolverFault.Throw);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "sa7");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        Assert.Equal(HttpStatusCode.Forbidden, (await DecisionHttp.Decide(factory, "alpha-sup-2", item, "reject")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "reject", " ")).StatusCode);
        Assert.Equal(0, factory.UserIdentity.CallCount);
    }
}
