using System.Net;
using System.Text.Json;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueReleaseTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ReasonOf(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    [Fact]
    public async Task QUE_06_claimant_releases_then_another_reviewer_claims_and_non_claimant_is_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "rel1");
        using var one = factory.CreateReviewerClient("alpha-sup-1");
        using var two = factory.CreateReviewerClient("alpha-norole");
        var item = Assert.Single(await ReviewerTenantTests.ListAsync(one)).GetProperty("item_id").GetGuid();
        var url = $"/v1/queue/items/{item}";

        // Unclaimed release.
        var notClaimed = await one.PostAsync(url + "/release", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, notClaimed.StatusCode);
        Assert.Equal("item_not_claimed", await ReasonOf(notClaimed));

        // No role is needed to claim (alpha-norole).
        Assert.Equal(HttpStatusCode.OK, (await two.PostAsync(url + "/claim", null, Ct)).StatusCode);

        var notClaimant = await one.PostAsync(url + "/release", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, notClaimant.StatusCode);
        Assert.Equal("not_claimant", await ReasonOf(notClaimant));

        var released = await two.PostAsync(url + "/release", null, Ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        using (var doc = JsonDocument.Parse(await released.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal("open", doc.RootElement.GetProperty("state").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, (await one.PostAsync(url + "/claim", null, Ct)).StatusCode);
    }
}
