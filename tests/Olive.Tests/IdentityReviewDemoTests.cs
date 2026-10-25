using System.Net;
using System.Text;
using System.Text.Json;

namespace Olive.Tests;

// D-18: bluto creates, a steward defers then accepts, bluto polls the decision with the cursor and withdraws a second item.
// The transcript carries HTTP status codes and static reason codes only.
[Trait("Category", "Integration")]
[Trait("Demo", "identity")]
public sealed class IdentityReviewDemoTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private const string AlphaToken = "fixture-service-token-bluto-alpha";
    private const string Rationale = "IDENTITY-DEMO-RATIONALE-SENTINEL-6139";
    private static readonly string RefA = Guid.NewGuid().ToString();
    private static readonly string RefB = Guid.NewGuid().ToString();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Body(string producerRef, string extra = "") =>
        "{" + extra + "\"kind\":\"identity_review\",\"subtype\":\"candidate_pair\",\"producer_ref\":\"" + producerRef +
        "\",\"subject_ref\":\"bluto:candidate/" + producerRef + "\",\"payload_ref\":\"bluto:review-evidence/" + producerRef +
        "\",\"ruleset_version\":\"rules-1\"}";

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private static string Decision(string decision) => JsonSerializer.Serialize(new Dictionary<string, string> { ["decision"] = decision, ["rationale"] = Rationale });

    [Fact]
    public async Task IRQ_demo_bluto_creates_steward_defers_and_accepts_bluto_polls_and_withdraws()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var bluto = factory.CreateServiceClient(AlphaToken);
        using var steward = factory.CreateReviewerClient("alpha-steward-1");
        var t = new StringBuilder("# Identity review transcript\n\n");
        var n = 0;
        void Step(string text) => t.Append(++n).Append(". ").Append(text).Append('\n');

        var create = await bluto.PostAsync(Items, ApiProposalTests.Json(Body(RefA)), Ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await Json(create);
        var itemA = created.GetProperty("item_id").GetGuid();
        Assert.Equal("open", created.GetProperty("state").GetString());
        Step("bluto creates an identity_review item: HTTP 201, state `open`");

        var replay = await bluto.PostAsync(Items, ApiProposalTests.Json(Body(RefA)), Ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(itemA, (await Json(replay)).GetProperty("item_id").GetGuid());
        Step("bluto replays the identical create: HTTP 200, same item");

        var assigned = await bluto.PostAsync(Items, ApiProposalTests.Json(Body(Guid.NewGuid().ToString(), "\"tenant_id\":\"x\",")), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, assigned.StatusCode);
        var reason = (await Json(assigned)).GetProperty("reason").GetString();
        Assert.Equal("server_assigned_field", reason);
        Step("bluto creates with a tenant_id field: HTTP 400, reason `" + reason + "`");

        Assert.Equal(HttpStatusCode.OK, (await steward.PostAsync($"{Items}/{itemA}/claim", null, Ct)).StatusCode);
        var defer = await steward.PostAsync($"{Items}/{itemA}/decision", ApiProposalTests.Json(Decision("defer")), Ct);
        Assert.Equal(HttpStatusCode.OK, defer.StatusCode);
        Assert.Equal("open", (await Json(defer)).GetProperty("state").GetString());
        Step("steward claims (HTTP 200) and defers: HTTP 200, state `open`");

        Assert.Equal(HttpStatusCode.OK, (await steward.PostAsync($"{Items}/{itemA}/claim", null, Ct)).StatusCode);
        var accept = await steward.PostAsync($"{Items}/{itemA}/decision", ApiProposalTests.Json(Decision("accept")), Ct);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var decided = await Json(accept);
        Assert.Equal("decided", decided.GetProperty("state").GetString());
        Step("steward re-claims (HTTP 200) and accepts: HTTP 200, state `decided`, decision `accept`");

        const string Poll = Items + "?kind=identity_review&state=decided";
        var first = await bluto.GetAsync(Poll, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = await Json(first);
        var row = Assert.Single(page.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(itemA, row.GetProperty("item_id").GetGuid());
        Assert.Equal("accept", row.GetProperty("decision").GetString());
        var cursor = page.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        Step("bluto polls without a cursor: HTTP 200, 1 decided item (accept), next_cursor present");

        var after = await bluto.GetAsync(Poll + "&decided_after=" + cursor, Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Empty((await Json(after)).GetProperty("items").EnumerateArray());
        Step("bluto polls with that cursor: HTTP 200, 0 items");

        var second = await bluto.PostAsync(Items, ApiProposalTests.Json(Body(RefB)), Ct);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var itemB = (await Json(second)).GetProperty("item_id").GetGuid();
        var withdraw = await bluto.PostAsync($"{Items}/{itemB}/withdraw", ApiProposalTests.Json("{\"reason\":\"source_changed\"}"), Ct);
        Assert.Equal(HttpStatusCode.OK, withdraw.StatusCode);
        Assert.Equal("withdrawn", (await Json(withdraw)).GetProperty("state").GetString());
        Step("bluto creates a second item (HTTP 201) and withdraws it: HTTP 200, state `withdrawn`");

        var last = await Json(await bluto.GetAsync(Poll, Ct));
        Assert.Equal(itemA, Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("item_id").GetGuid());
        Step("bluto polls again: HTTP 200, still only the first item");

        var dir = Path.Combine(TestPaths.Root, "TestResults");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "identity-review-transcript.md");
        await File.WriteAllTextAsync(path, t.ToString(), Ct);

        var written = await File.ReadAllTextAsync(path, Ct);
        Assert.Contains("server_assigned_field", written);
        var scan = new SentinelScan();
        scan.AddSource("transcript", written);
        scan.AddSource("logs", factory.Logs.Text);
        scan.AssertAbsent(Rationale, AlphaToken, RefA, RefB, "alpha-steward-1");
    }

    [Fact]
    public void IRQ_demo_runner_script_filters_to_the_identity_trait()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "build", "demo-identity-review.sh"));
        Assert.StartsWith("#!/usr/bin/env bash", text);
        Assert.Contains("set -euo pipefail", text);
        Assert.Contains("Demo=identity", text);
        Assert.Contains("identity-review-transcript.md", text);
    }
}
