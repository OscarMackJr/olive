using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ReviewerTenantTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    internal static async Task<Guid> SubmitAsync(HttpClient agent, string run)
    {
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.BodyFor(run)), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        using var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("proposal_id").GetGuid();
    }

    internal static async Task<JsonElement[]> ListAsync(HttpClient reviewer, string query = "")
    {
        var response = await reviewer.GetAsync(Items + query, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public async Task APR_01_valid_reviewer_lists_and_missing_or_unknown_is_401_on_real_routes()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var ok = factory.CreateReviewerClient("alpha-sup-1");
        Assert.Equal(HttpStatusCode.OK, (await ok.GetAsync(Items, Ct)).StatusCode);

        using var anon = factory.CreateClient();
        using var unknown = factory.CreateReviewerClient("no-such-reviewer");
        foreach (var client in new[] { anon, unknown })
        {
            foreach (var path in new[] { Items, Items + "/" + Guid.NewGuid() })
            {
                var response = await client.GetAsync(path, Ct);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("reviewer_unauthenticated", await ReasonAsync(response));
            }
        }
    }

    [Fact]
    public async Task Caller_separation_agent_key_on_real_list_route_is_403_with_zero_introspection()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var before = factory.Introspector.Calls;
        var response = await client.GetAsync(Items, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", await ReasonAsync(response));
        Assert.Equal(before, factory.Introspector.Calls);
    }

    [Theory]
    [InlineData("two-tenant")]
    [InlineData("no-tenant")]
    public async Task ReviewerTenant_unresolved_mapping_is_403_on_every_queue_route(string reviewerId)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateReviewerClient(reviewerId);
        foreach (var path in new[] { Items, Items + "/" + Guid.NewGuid() })
        {
            var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("reviewer_tenant_unresolved", await ReasonAsync(response));
        }
    }

    [Fact]
    public async Task ReviewerTenant_spoofed_query_header_and_body_values_are_ignored()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.BetaKey);
        var betaProposal = await SubmitAsync(agent, "spoof-b");

        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        alpha.DefaultRequestHeaders.Add("X-Tenant-Id", ApiProposalTests.Beta.ToString());
        Assert.Empty(await ListAsync(alpha, "?tenant_id=" + ApiProposalTests.Beta));

        using var beta = factory.CreateReviewerClient("beta-sup");
        var betaItems = await ListAsync(beta);
        var item = Assert.Single(betaItems);
        var itemId = item.GetProperty("item_id").GetGuid();
        Assert.Equal(betaProposal, item.GetProperty("producer_ref").GetGuid());

        var spoofed = await alpha.GetAsync($"{Items}/{itemId}?tenant_id={ApiProposalTests.Beta}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, spoofed.StatusCode);
        var withBody = new HttpRequestMessage(HttpMethod.Get, $"{Items}/{itemId}")
        {
            Content = ApiProposalTests.Json("{\"tenant_id\":\"" + ApiProposalTests.Beta + "\"}"),
        };
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.SendAsync(withBody, Ct)).StatusCode);
    }

    [Fact]
    public async Task D_03_no_create_or_withdraw_route_exists()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var routes = factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        var queueRoutes = routes.Where(r => r.RoutePattern.RawText!.StartsWith(Items, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(queueRoutes);
        foreach (var route in queueRoutes)
        {
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            var raw = route.RoutePattern.RawText!;
            if (!raw.EndsWith("/claim", StringComparison.OrdinalIgnoreCase) && !raw.EndsWith("/release", StringComparison.OrdinalIgnoreCase) && !raw.EndsWith("/decision", StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain("POST", methods);
            }
            Assert.False(route.RoutePattern.RawText!.EndsWith("/withdraw", StringComparison.OrdinalIgnoreCase));
        }

        using var client = factory.CreateReviewerClient("alpha-sup-1");
        var post = await client.PostAsync(Items, ApiProposalTests.Json("{}"), Ct);
        Assert.False(post.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ReviewerTenant_get_item_in_tenant_is_200_and_foreign_or_missing_are_indistinguishable_404()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await SubmitAsync(agent, "get-a");
        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var itemId = Assert.Single(await ListAsync(alpha)).GetProperty("item_id").GetGuid();

        var ok = await alpha.GetAsync($"{Items}/{itemId}", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var okDoc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync(Ct));
        Assert.Equal(itemId, okDoc.RootElement.GetProperty("item_id").GetGuid());

        using var beta = factory.CreateReviewerClient("beta-sup");
        var foreign = await beta.GetAsync($"{Items}/{itemId}", Ct);
        var missing = await beta.GetAsync($"{Items}/{Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("item_not_found", await ReasonAsync(foreign));
        Assert.Equal(await foreign.Content.ReadAsStringAsync(Ct), await missing.Content.ReadAsStringAsync(Ct));
    }
}
