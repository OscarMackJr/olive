using System.Net;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ReviewerPagesListTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UI_02_review_lists_own_tenant_open_items_oldest_first_and_never_another_tenants()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alphaAgent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var betaAgent = factory.CreateKeyClient(ApiProposalTests.BetaKey);
        await ReviewerTenantTests.SubmitAsync(alphaAgent, "ui-a1");
        await ReviewerTenantTests.SubmitAsync(alphaAgent, "ui-a2");
        await ReviewerTenantTests.SubmitAsync(alphaAgent, "ui-a3");
        await ReviewerTenantTests.SubmitAsync(betaAgent, "ui-b1");

        using var api = factory.CreateReviewerClient("alpha-sup-1");
        var alphaIds = (await ReviewerTenantTests.ListAsync(api)).Select(i => i.GetProperty("item_id").GetGuid()).ToArray();
        Assert.Equal(3, alphaIds.Length);
        using var betaApi = factory.CreateReviewerClient("beta-sup");
        var betaId = Assert.Single(await ReviewerTenantTests.ListAsync(betaApi)).GetProperty("item_id").GetGuid();

        using var ui = await factory.SignInDevAsync("alpha-sup-1");
        var response = await ui.GetAsync("/review", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Ct);
        var positions = alphaIds.Select(id => html.IndexOf("/review/items/" + id, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p).ToArray(), positions);
        Assert.DoesNotContain(betaId.ToString(), html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=\"", html);
        foreach (var heading in new[] { "Created", "Age", "Kind", "Subtype", "Subject", "State", "Expires", "Item" })
        {
            Assert.Contains(">" + heading + "</th>", html);
        }
    }

    [Fact]
    public async Task UI_02_claimed_items_are_absent_from_the_open_list()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "ui-c1");
        await ReviewerTenantTests.SubmitAsync(agent, "ui-c2");
        using var api = factory.CreateReviewerClient("alpha-sup-1");
        var ids = (await ReviewerTenantTests.ListAsync(api)).Select(i => i.GetProperty("item_id").GetGuid()).ToArray();
        var claim = await api.PostAsync("/v1/queue/items/" + ids[0] + "/claim", null, Ct);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        using var ui = await factory.SignInDevAsync("alpha-sup-2");
        var html = await ui.GetStringAsync("/review", Ct);
        Assert.DoesNotContain(ids[0].ToString(), html);
        Assert.Contains("/review/items/" + ids[1], html);
    }

    [Fact]
    public async Task UI_02_empty_tenant_sees_the_empty_state_copy()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "ui-e1");
        using var ui = await factory.SignInDevAsync("beta-sup");
        var html = await ui.GetStringAsync("/review", Ct);
        Assert.Contains("No open items", html);
        Assert.Contains("There is nothing to review in your tenant right now.", html);
        Assert.DoesNotContain("/review/items/", html);
    }

    [Theory]
    [InlineData("no-tenant")]
    [InlineData("two-tenant")]
    public async Task UI_01_session_without_exactly_one_tenant_gets_403_with_a_static_reason_and_a_sign_out_button(string reviewerId)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "ui-f1");
        using var ui = await factory.SignInDevAsync(reviewerId);
        var response = await ui.GetAsync("/review", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Reason code: no_reviewer_tenant", html);
        Assert.Contains("No reviewer tenant for your account", html);
        Assert.Contains("action=\"/auth/sign-out\"", html);
        Assert.Contains("Sign out", html);
        Assert.DoesNotContain("/review/items/", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UI_01_root_redirects_to_review_and_v1_routes_ignore_the_reviewer_cookie()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var ui = await factory.SignInDevAsync("alpha-sup-1");
        var root = await ui.GetAsync("/", Ct);
        Assert.Equal("/review", root.Headers.Location?.OriginalString);
        var api = await ui.GetAsync("/v1/queue/items", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
    }
}
