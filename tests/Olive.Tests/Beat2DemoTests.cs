using System.Net;
using System.Text;
using System.Text.Json;
using Olive.Application;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

// Beat 2 (EVD-02): a fixture-traced proposal is approved, executes in simulation, and the chain is read from the execution record.
[Trait("Category", "Integration")]
[Trait("Demo", "beat2")]
public sealed class Beat2DemoTests(PostgresFixture fixture)
{
    private const string JustSentinel = "SENTINEL-BEAT2-JUSTIFICATION-7741";
    private const string TokenSentinel = "SENTINEL-BEAT2-SERVICE-TOKEN-5203";
    private const string Rationale = "Checked the case note reference against the source record.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EVD_02_fixture_traced_proposal_is_approved_executed_and_chain_queried_from_the_execution_record()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        // T-06-29: issue a known sentinel service token so the transcript scan can prove it is absent.
        var keys = StubKeyIntrospector.CreateDefault(TimeProvider.System);
        await using var factory = new OliveApiFactory(db, introspector: keys, innerIssuer: new ContainmentTests.FixedTokenIssuer(keys, TokenSentinel));
        var t = new StringBuilder("# Beat 2 transcript: approved action executes in simulation and the chain is read from the execution record\n\n");

        // Step 1: the agent submits an in-purpose proposal.
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor("beat2").Replace("\"justification\":\"j\"", "\"justification\":\"" + JustSentinel + "\"");
        Assert.Contains(JustSentinel, body);
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        using var postDoc = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct));
        var proposal = postDoc.RootElement.GetProperty("proposal_id").GetGuid();
        Assert.Equal("awaiting_approval", postDoc.RootElement.GetProperty("state").GetString());
        await db.AdminExecAsync($"update proposal set on_behalf_of = 'analyst.alpha@example.test' where proposal_id = '{proposal}'");
        t.Append("1. Agent `liam` submits an in-purpose `crm.case_note@1` proposal: HTTP 201, proposal_id `").Append(proposal).Append("`, state `awaiting_approval`\n");

        // Step 2: fixture trace verification, stated plainly.
        var verification = await DecisionHttp.Scalar<string>(db, "select trace_verification from proposal where proposal_id = @p", ("p", proposal));
        Assert.False(string.IsNullOrEmpty(verification));
        t.Append("2. Trace verification used fixtures, not a live hometown (trace_verification = `").Append(verification)
            .Append("`). This is the Decision section 6 exit note: the trace is verified against fixture data only.\n");

        // Step 3: the reviewer lists, claims and approves.
        using var reviewer = factory.CreateReviewerClient("alpha-sup-1");
        var items = await ReviewerTenantTests.ListAsync(reviewer);
        var item = items.Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid();
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        var decision = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve", Rationale);
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        t.Append("3. Reviewer `alpha-sup-1` lists ").Append(items.Length).Append(" open item(s), claims item `").Append(item)
            .Append("` and approves with rationale \"").Append(Rationale).Append("\": HTTP 200\n");

        // Step 4: execution ran in simulation.
        using var got = JsonDocument.Parse(await (await agent.GetAsync($"/v1/proposals/{proposal}", Ct)).Content.ReadAsStringAsync(Ct));
        Assert.Equal("executed", got.RootElement.GetProperty("state").GetString());
        var mode = await DecisionHttp.Scalar<string>(db, "select execution_mode from execution_ledger where proposal_id = @p and phase = 'finished'", ("p", proposal));
        Assert.Equal("simulated", mode);
        t.Append("4. Proposal GET shows state `executed`; ledger execution_mode is `").Append(mode).Append("`. No network write occurred: the executor only appends a JSONL line.\n");

        // Step 5: the exact single SELECT against the chain view, run as olive_reader with the tenant set.
        var execution = await DecisionHttp.Scalar<Guid>(db, "select execution_id from execution_ledger where proposal_id = @p and phase = 'started'", ("p", proposal));
        var rows = await ExecutionChainViewTests.QueryChainAsync(db.ReaderConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, execution);
        var row = Assert.Single(rows);
        Assert.Equal("olive:proposal/" + proposal, row["proposal_ref"]);
        Assert.NotNull(row["trace_verification"]);
        Assert.NotNull(row["policy_decision"]);
        Assert.Equal(Rationale, row["rationale"]);
        Assert.False(string.IsNullOrEmpty((string?)row["decided_by"]));
        Assert.Equal("succeeded", row["outcome"]);
        t.Append("5. Chain query as `olive_reader` (tenant set to alpha, execution_id `").Append(execution).Append("`):\n\n```sql\n")
            .Append(ExecutionChainViewTests.ChainSelect).Append(";\n```\n\nResult (1 row):\n\n```\n");
        foreach (var (key, value) in row) { t.Append(key).Append(": ").Append(value ?? "(null)").Append('\n'); }
        t.Append("```\n\n");

        // Step 6: the JSONL line.
        var jsonl = (await File.ReadAllTextAsync(factory.ExecutorOutputPath, Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(jsonl);
        t.Append("6. Executor output line (").Append(Path.GetFileName(factory.ExecutorOutputPath)).Append("):\n\n```\n").Append(line).Append("\n```\n");

        var dir = Path.Combine(TestPaths.Root, "TestResults");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "beat2-transcript.md");
        await File.WriteAllTextAsync(path, t.ToString(), Ct);

        var written = await File.ReadAllTextAsync(path, Ct);
        Assert.False(string.IsNullOrWhiteSpace(written));
        Assert.Contains(ExecutionChainViewTests.ChainSelect, written);
        Assert.Contains(line, written);
        Assert.Contains("fixture", written);
        var scan = new SentinelScan();
        scan.AddSource("transcript", written);
        Assert.Contains(factory.OutboundCalls, c => c.Port == nameof(ITokenIssuer));
        scan.AssertAbsent(ApiProposalTests.AlphaKey, TokenSentinel, JustSentinel);
    }

    [Fact]
    public void EVD_02_runner_script_filters_to_the_beat2_trait()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "build", "demo-beat2.sh"));
        Assert.StartsWith("#!/usr/bin/env bash", text);
        Assert.Contains("set -euo pipefail", text);
        Assert.Contains("Demo=beat2", text);
    }
}
