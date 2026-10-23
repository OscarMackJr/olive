using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Olive.Application;
using Olive.Host.Auth;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ReviewerAuthTests(PostgresFixture fixture)
{
    private const string Probe = "/v1/queue/_probe";

    private static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    private static async Task<JsonDocument> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task APR_01_valid_reviewer_authenticates_with_stub_auth_source()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateReviewerClient("alpha-sup-1");
        var response = await client.GetAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await BodyAsync(response);
        Assert.Equal("stub", body.RootElement.GetProperty("auth_source").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no-such-reviewer")]
    [InlineData("ALPHA-SUP-1")]
    [InlineData("")]
    public async Task APR_01_missing_or_unknown_reviewer_is_401_reviewer_unauthenticated(string? id)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateClient();
        if (id is not null) { client.DefaultRequestHeaders.Add(DevReviewerAuthenticationHandler.HeaderName, id); }
        var response = await client.GetAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("reviewer_unauthenticated", await ReasonAsync(response));
    }

    [Fact]
    public async Task Caller_separation_agent_key_on_reviewer_route_is_403_and_never_introspected()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var before = factory.Introspector.Calls;
        var response = await client.GetAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", await ReasonAsync(response));
        Assert.Equal(before, factory.Introspector.Calls);
    }

    [Fact]
    public async Task Caller_separation_reviewer_on_proposals_is_403()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateReviewerClient("alpha-sup-1");
        var response = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", await ReasonAsync(response));
        var get = await client.GetAsync("/v1/proposals/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task Caller_separation_no_credentials_is_401_on_both_surfaces()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateClient();
        var proposals = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, proposals.StatusCode);
        Assert.Equal("key_inactive", await ReasonAsync(proposals));
        var probe = await client.GetAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
        Assert.Equal("reviewer_unauthenticated", await ReasonAsync(probe));
    }

    [Fact]
    public async Task Caller_separation_both_credentials_each_surface_is_decided_by_its_own_scheme()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        client.DefaultRequestHeaders.Add(DevReviewerAuthenticationHandler.HeaderName, "alpha-sup-1");

        var before = factory.Introspector.Calls;
        var probe = await client.GetAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.Equal(before, factory.Introspector.Calls);

        // The agent scheme alone decides on the proposals surface: the key is introspected and the request is not a 401/403.
        var get = await client.GetAsync("/v1/proposals/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.True(factory.Introspector.Calls > before);
    }

    [Fact]
    public async Task APR_05_request_cannot_influence_the_auth_source_claim()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateReviewerClient("alpha-sup-1");
        client.DefaultRequestHeaders.Add("X-Auth-Source", "entra");
        var response = await client.GetAsync(Probe + "?auth_source=entra", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await BodyAsync(response);
        Assert.Equal("stub", body.RootElement.GetProperty("auth_source").GetString());
    }

    [Theory]
    [InlineData("two-tenant", false)]
    [InlineData("no-tenant", false)]
    [InlineData("alpha-sup-1", true)]
    public async Task ReviewerClaim_tenant_is_set_only_when_exactly_one_tenant_maps(string reviewerId, bool expectTenant)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateReviewerClient(reviewerId);
        var response = await client.GetAsync(Probe + "?tenant_id=" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await BodyAsync(response);
        Assert.Equal(expectTenant, body.RootElement.GetProperty("has_tenant").GetBoolean());
        if (expectTenant)
        {
            Assert.Equal(ApiProposalTests.Alpha.ToString("D"), body.RootElement.GetProperty("tenant_id").GetString());
        }
    }
}
