using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class AuditPageTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Justification = "AUDIT-JUSTIFICATION-SENTINEL-5521";

    // Same path as DecisionHttp.SubmitAsync but with a distinctive justification so absence can be asserted.
    private static async Task<(Guid Item, Guid Proposal)> SeedAsync(OliveApiFactory factory, TestDatabase db, string run)
    {
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor(run).Replace("\"justification\":\"j\"", "\"justification\":\"" + Justification + "\"") ;
        body = body.TrimEnd('}') + ",\"expires_in_seconds\":3600}";
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var proposal = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();
        await db.AdminExecAsync($"update proposal set on_behalf_of = 'analyst.alpha@example.test' where proposal_id = '{proposal}'");
        using var rev = factory.CreateReviewerClient("alpha-sup-1");
        var items = await ReviewerTenantTests.ListAsync(rev);
        var item = items.Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid();
        return (item, proposal);
    }

    private static async Task<(Guid Item, Guid Proposal)> ApproveAsync(OliveApiFactory factory, TestDatabase db, string run)
    {
        var (item, proposal) = await SeedAsync(factory, db, run);
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);
        return (item, proposal);
    }

    [Fact]
    public async Task UI_05_role_holder_sees_fields_hashes_match_chain_and_ordered_history()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await ApproveAsync(factory, db, "au1");
        var bodySha = await DecisionHttp.Scalar<string>(db, "select body_sha256 from proposal where proposal_id = @p", ("p", proposal));
        var proposalHash = await DecisionHttp.Scalar<string>(db, "select proposal_hash from proposal where proposal_id = @p", ("p", proposal));
        Assert.Matches("^[0-9a-f]{64}$", bodySha!);

        using var ui = await factory.SignInDevAsync("alpha-sup-2");
        var response = await ui.GetAsync($"/review/items/{item}/audit", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("crm.case_note@1", html);
        Assert.Contains("party://p1", html);
        Assert.Contains("payload://c1", html);
        Assert.Contains(Justification, html);
        Assert.Contains(bodySha!, html);
        Assert.Contains(proposalHash!, html);
        Assert.Contains("Matches: recomputed proposal_hash equals the stored value.", html);
        Assert.DoesNotContain("Does not match", html);
        Assert.Contains("Execution chain", html);
        Assert.Contains("simulated", html);
        Assert.Contains("succeeded", html);
        Assert.Contains("aaaaaaaa-0000-4000-8000-000000000001", html);
        Assert.DoesNotContain("No execution recorded yet.", html);

        var seqs = Regex.Matches(html, "<tr>\\s*<td>(\\d+)</td>\\s*<td>[a-z_]+</td>").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        Assert.True(seqs.Length >= 2);
        Assert.Equal(seqs.OrderBy(s => s).ToArray(), seqs);
        Assert.Equal(1, seqs[0]);
        Assert.DoesNotContain("DetailJson", html);
        Assert.DoesNotContain("detail", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UI_05_reviewer_without_the_role_sees_hashes_and_indicator_but_no_field_values()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await SeedAsync(factory, db, "au2");
        var proposalHash = await DecisionHttp.Scalar<string>(db, "select proposal_hash from proposal where proposal_id = @p", ("p", proposal));

        using var ui = await factory.SignInDevAsync("alpha-norole");
        var html = await ui.GetStringAsync($"/review/items/{item}/audit", Ct);

        Assert.Contains(proposalHash!, html);
        Assert.Contains("Matches:", html);
        Assert.Contains("You do not hold the role required", html);
        Assert.DoesNotContain(Justification, html);
        Assert.DoesNotContain("payload://c1", html);
        Assert.DoesNotContain("Execution chain", html);
    }

    [Fact]
    public async Task UI_05_approved_item_without_a_ledger_row_says_no_execution_recorded_yet()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        FaultingExecutionStore? store = null;
        await using var factory = new OliveApiFactory(db, configureServices: s => s.Decorate<IExecutionStore>(real => store = new FaultingExecutionStore(real)));
        _ = factory.Services.GetRequiredService<ExecutionService>();
        store!.Mode = ExecutionFault.ThrowOnBegin;
        var (item, proposal) = await ApproveAsync(factory, db, "au3");
        Assert.Equal(0L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p", ("p", proposal)));

        using var ui = await factory.SignInDevAsync("alpha-sup-2");
        var html = await ui.GetStringAsync($"/review/items/{item}/audit", Ct);

        Assert.Contains("No execution recorded yet.", html);
    }

    [Fact]
    public async Task UI_05_rejected_item_has_no_execution_section()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await SeedAsync(factory, db, "au4");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "reject")).StatusCode);

        using var ui = await factory.SignInDevAsync("alpha-sup-2");
        var html = await ui.GetStringAsync($"/review/items/{item}/audit", Ct);

        Assert.DoesNotContain("Execution chain", html);
        Assert.DoesNotContain("No execution recorded yet.", html);
        Assert.Contains("Matches:", html);
    }

    [Fact]
    public async Task UI_05_tampered_stored_justification_reads_does_not_match()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await ApproveAsync(factory, db, "au5");
        await db.AdminExecAsync($"update proposal set justification = justification || 'x' where proposal_id = '{proposal}'");

        using var ui = await factory.SignInDevAsync("alpha-sup-2");
        var html = await ui.GetStringAsync($"/review/items/{item}/audit", Ct);

        Assert.Contains("Does not match: recomputed proposal_hash differs from the stored value. Do not rely on this record.", html);
        Assert.DoesNotContain("Matches:", html);
    }

    [Fact]
    public async Task UI_05_cross_tenant_and_unknown_item_ids_give_404()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await ApproveAsync(factory, db, "au6");

        using var beta = await factory.SignInDevAsync("beta-sup");
        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"/review/items/{item}/audit", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"/review/items/{Guid.NewGuid()}/audit", Ct)).StatusCode);
    }

    [Fact]
    public async Task UI_05_fixture_trace_banner_shows_for_fixture_verified_proposals()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await SeedAsync(factory, db, "au7");
        var verification = await DecisionHttp.Scalar<string>(db, "select trace_verification from proposal where proposal_id = @p", ("p", proposal));

        using var ui = await factory.SignInDevAsync("alpha-sup-2");
        var html = await ui.GetStringAsync($"/review/items/{item}/audit", Ct);

        Assert.Equal(verification == "fixture", html.Contains("Fixture trace:"));
    }

    [Fact]
    public async Task UI_05_audit_page_adds_no_http_endpoint_and_no_write_verbs()
    {
        var pages = Path.Combine(TestPaths.Root, "src", "Olive.Host", "Pages");
        foreach (var file in Directory.EnumerateFiles(pages, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file, Ct);
            Assert.DoesNotContain("MapGet", text);
            Assert.DoesNotContain("MapPost", text);
        }

        var sql = await File.ReadAllTextAsync(Path.Combine(TestPaths.Root, "src", "Olive.Persistence", "AuditSql.cs"), Ct);
        foreach (var verb in new[] { "insert", "update", "delete" }) { Assert.DoesNotContain(verb, sql, StringComparison.OrdinalIgnoreCase); }
        var cshtml = await File.ReadAllTextAsync(Path.Combine(pages, "Review", "Audit.cshtml"), Ct);
        Assert.DoesNotContain("DetailJson", cshtml);
        Assert.DoesNotContain("Html.Raw", cshtml);
    }
}
