using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Catalog;

namespace Olive.Tests;

// Beat 4 (EVD-03): an out-of-purpose proposal is refused before any human sees it, the refusal is traceable, and it never reaches the queue.
[Trait("Category", "Integration")]
[Trait("Demo", "beat4")]
public sealed class Beat4DemoTests(PostgresFixture fixture)
{
    private const string ParamSentinel = "SENTINEL-BEAT4-PARAM-5521";
    private const string JustSentinel = "SENTINEL-BEAT4-JUSTIFICATION-5521";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The fixture key's purpose is "servicing"; this catalog only permits "collections".
    private static CatalogSet MixedPurposeCatalog()
    {
        var any = SchemaValidator.Compile("{\"type\":\"object\"}");
        return new([new CatalogRelease(1,
        [
            new CatalogEntry("crm.case_note@1", JsonDocument.Parse("{}").RootElement, any, RequiredPurpose: ["collections"]),
        ])]);
    }

    private static string Envelope(string action, string run) =>
        "{\"run_id\":\"" + run + "\",\"action_type\":\"" + action + "\",\"party_ref\":\"party://p1\",\"parameters\":{\"case_ref\":\"" + ParamSentinel
        + "\",\"note_ref\":\"payload://n1\"},\"justification\":\"" + JustSentinel + "\",\"trace_ref\":\"trace://t1\"}";

    private static Task<T?> Scalar<T>(TestDatabase db, string sql, Guid id) =>
        ApiProposalTests.ReaderScalarAsync<T>(db, TestPaths.TenantAlpha, sql, ("p", id));

    [Fact]
    public async Task EVD_03_out_of_purpose_proposal_is_refused_traceable_and_never_queued()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(MixedPurposeCatalog()));
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var t = new StringBuilder("# Beat 4 transcript: policy refusal before review\n\n");

        // Step 1: the refused submission.
        var refused = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(Envelope("crm.case_note@1", "beat4-refused")), Ct);
        Assert.Equal(HttpStatusCode.Created, refused.StatusCode);
        using var refusedDoc = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        var body = refusedDoc.RootElement;
        var refusedId = body.GetProperty("proposal_id").GetGuid();
        Assert.Equal("refused_policy", body.GetProperty("state").GetString());
        Assert.Equal("purpose_not_permitted", body.GetProperty("refusal_reason").GetString());
        t.Append("1. Submit out-of-purpose proposal: HTTP 201, proposal_id `").Append(refusedId).Append("`, state `refused_policy`, refusal_reason `purpose_not_permitted`\n");

        // Step 2: the stored row.
        var state = await Scalar<string>(db, "select state from proposal where proposal_id = @p", refusedId);
        var reason = await Scalar<string>(db, "select refusal_reason from proposal where proposal_id = @p", refusedId);
        Assert.Equal("refused_policy", state);
        Assert.Equal("purpose_not_permitted", reason);
        t.Append("2. Stored proposal row: state `").Append(state).Append("`, refusal_reason `").Append(reason).Append("`\n");

        // Step 3: its event trail, with policy check names and outcomes only.
        var events = await Scalar<string>(db, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", refusedId);
        Assert.Contains("policy_evaluated", events);
        var detail = await Scalar<string>(db, "select detail::text from proposal_event where proposal_id = @p and event_type = 'policy_evaluated'", refusedId);
        using var detailDoc = JsonDocument.Parse(detail!);
        var checks = detailDoc.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() + "=" + c.GetProperty("result").GetString())
            .ToArray();
        Assert.Contains(checks, c => c == "purpose=fail");
        t.Append("3. proposal_event rows: `").Append(events).Append("`; policy_evaluated outcome `").Append(detailDoc.RootElement.GetProperty("outcome").GetString())
            .Append("`, checks: ").Append(string.Join(", ", checks.Select(c => "`" + c + "`"))).Append('\n');

        // Step 4: no queue item.
        var queued = await Scalar<long>(db, "select count(*) from queue_item where proposal_id = @p or producer_ref = @p", refusedId);
        Assert.Equal(0, queued);
        t.Append("4. queue_item rows for this proposal: ").Append(queued).Append('\n');

        // Step 5: contrast with an in-purpose proposal, and the reviewer view.
        // A second host over the same database runs the default catalog, which permits the key's purpose.
        await using var okFactory = new OliveApiFactory(db);
        using var okAgent = okFactory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var okReviewer = okFactory.CreateReviewerClient("alpha-sup-1");
        var okResponse = await okAgent.PostAsync("/v1/proposals", ApiProposalTests.Json(Envelope("crm.case_note@1", "beat4-ok")), Ct);
        Assert.Equal(HttpStatusCode.Created, okResponse.StatusCode);
        using var okDoc = JsonDocument.Parse(await okResponse.Content.ReadAsStringAsync(Ct));
        var okId = okDoc.RootElement.GetProperty("proposal_id").GetGuid();
        var items = await ReviewerTenantTests.ListAsync(okReviewer);
        Assert.DoesNotContain(items, i => i.GetProperty("producer_ref").GetGuid() == refusedId);
        Assert.DoesNotContain(items, i => i.ToString().Contains(refusedId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.True(okDoc.RootElement.GetProperty("state").GetString() == "awaiting_approval", okDoc.RootElement.GetRawText());
        var listed = Assert.Single(items, i => i.GetProperty("producer_ref").GetGuid() == okId);
        Assert.Equal("open", listed.GetProperty("state").GetString());
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item where producer_ref = @p and state = 'open'", okId));
        t.Append("5. Reviewer `alpha-sup-1` lists ").Append(items.Length).Append(" open item(s): refused proposal `").Append(refusedId)
            .Append("` is absent; in-purpose proposal `").Append(okId).Append("` is listed with exactly one open item\n");

        var dir = Path.Combine(TestPaths.Root, "TestResults");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "beat4-transcript.md");
        await File.WriteAllTextAsync(path, t.ToString(), Ct);

        var written = await File.ReadAllTextAsync(path, Ct);
        Assert.Contains("purpose_not_permitted", written);
        var scan = new SentinelScan();
        scan.AddSource("transcript", written);
        scan.AssertAbsent(ParamSentinel, JustSentinel, ApiProposalTests.AlphaKey);
    }

    [Fact]
    public void EVD_03_runner_script_filters_to_the_beat4_trait()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "build", "demo-beat4.sh"));
        Assert.StartsWith("#!/usr/bin/env bash", text);
        Assert.Contains("set -euo pipefail", text);
        Assert.Contains("Demo=beat4", text);
    }
}
