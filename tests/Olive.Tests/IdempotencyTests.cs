using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class IdempotencyTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Post(HttpClient c, string body) =>
        c.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static string WithJustification(string runId, string justification) =>
        ApiProposalTests.BodyFor(runId).Replace("\"justification\":\"j\"", "\"justification\":\"" + justification + "\"");

    [Fact]
    public async Task API_06_identical_resubmission_replays_200_with_original_id_and_current_state()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor("r1");

        var first = await Post(alpha, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = (await ReadAsync(first)).GetProperty("proposal_id").GetGuid();

        var second = await Post(alpha, body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var j = await ReadAsync(second);
        Assert.Equal(id, j.GetProperty("proposal_id").GetGuid());
        Assert.Equal("proposed", j.GetProperty("state").GetString());
        Assert.Equal($"/v1/proposals/{id}", second.Headers.Location!.OriginalString);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IProposalStore>();
            Assert.True(await store.TransitionAsync(ApiProposalTests.Alpha, id, "proposed", "approved", "reviewer", Ct));
        }

        var third = await Post(alpha, body);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var j3 = await ReadAsync(third);
        Assert.Equal(id, j3.GetProperty("proposal_id").GetGuid());
        Assert.Equal("approved", j3.GetProperty("state").GetString());
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Fact]
    public async Task API_06_replay_is_canonical_key_order_whitespace_and_number_spelling_insensitive()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(ApiProposalTests.PermissiveCatalog()));
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        const string a = "{\"run_id\":\"r2\",\"action_type\":\"test.open@1\",\"party_ref\":\"party://p1\",\"parameters\":{\"x\":1.0,\"y\":\"z\"},\"justification\":\"j\",\"trace_ref\":\"trace://t1\"}";
        const string b = "{ \"trace_ref\" : \"trace://t1\", \"justification\":\"j\",\n \"parameters\":{\"y\":\"z\",\"x\":1}, \"party_ref\":\"party://p1\", \"action_type\":\"test.open@1\", \"run_id\":\"r2\" }";
        Assert.Equal(HttpStatusCode.Created, (await Post(alpha, a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(alpha, b)).StatusCode);
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Fact]
    public async Task API_06_different_body_same_run_gives_409_not_stored_and_logged_value_free()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var first = await Post(alpha, ApiProposalTests.BodyFor("secret-run-key-77"));
        var id = (await ReadAsync(first)).GetProperty("proposal_id").GetGuid();

        var r = await Post(alpha, WithJustification("secret-run-key-77", "confidential-justification-text"));
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Conflict, ProblemReasons.IdempotencyConflict);
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));

        var log = factory.Logs.Text;
        Assert.Contains(id.ToString(), log);
        foreach (var forbidden in new[] { "secret-run-key-77", "confidential-justification-text", "payload://c1", "payload://n1", "party://p1", ApiProposalTests.AlphaKey })
        {
            Assert.DoesNotContain(forbidden, log);
        }
    }

    [Fact]
    public async Task API_06_nono_session_id_only_change_is_a_different_body_and_gets_409()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.Created, (await Post(alpha, ApiProposalTests.BodyFor("r3", "nono-a"))).StatusCode);
        var r = await Post(alpha, ApiProposalTests.BodyFor("r3", "nono-b"));
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Conflict, ProblemReasons.IdempotencyConflict);
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Theory]
    [InlineData("{\"run_id\":\"\\ud800\"}")]
    [InlineData("{\"run_id\":\"r\",\"run_id\":\"s\"}")]
    [InlineData("{\"run_id\":\"r\",\"parameters\":{\"x\":12345678901234567890}}")]
    [InlineData("{\"action_type\":\"crm.case_note@1\"}")]
    [InlineData("{\"run_id\":\"\"}")]
    [InlineData("not json")]
    public async Task API_06_gate_failures_give_400_before_any_lookup(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await Post(alpha, body);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.InvalidRequest);
        Assert.DoesNotContain("FindByRun", factory.Events);
        Assert.DoesNotContain("insert", factory.Events);
    }

    [Fact]
    public async Task API_06_idempotency_precedes_schema_and_replay_does_not_insert()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor("r5");
        Assert.Equal(HttpStatusCode.Created, (await Post(alpha, body)).StatusCode);

        // Same run, different hash, schema-invalid (justification missing): 409 wins over 400.
        var invalidDifferent = "{\"run_id\":\"r5\",\"action_type\":\"crm.case_note@1\"}";
        var r = await Post(alpha, invalidDifferent);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Conflict, ProblemReasons.IdempotencyConflict);

        factory.Events.Clear();
        Assert.Equal(HttpStatusCode.OK, (await Post(alpha, body)).StatusCode);
        Assert.Contains("FindByRun", factory.Events);
        Assert.DoesNotContain("insert", factory.Events);
    }

    [Fact]
    public async Task API_06_schema_invalid_first_submission_is_not_stored_and_corrected_resubmit_is_201()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await Post(alpha, "{\"run_id\":\"r6\",\"action_type\":\"crm.case_note@1\"}");
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.SchemaViolation);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        Assert.Equal(HttpStatusCode.Created, (await Post(alpha, ApiProposalTests.BodyFor("r6"))).StatusCode);
    }

    [Fact]
    public async Task API_06_idempotency_is_scoped_to_tenant()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var beta = factory.CreateKeyClient(ApiProposalTests.BetaKey);
        Assert.Equal(HttpStatusCode.Created, (await Post(alpha, ApiProposalTests.BodyFor("r7"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(beta, WithJustification("r7", "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(alpha, ApiProposalTests.BodyFor("r7"))).StatusCode);
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Beta));
    }

    [Fact]
    public async Task API_06_stored_canonical_body_and_hashes_are_audit_recomputable()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor("r8", "nono-8");
        var id = (await ReadAsync(await Post(alpha, body))).GetProperty("proposal_id").GetGuid();

        var canonical = await ApiProposalTests.ReaderScalarAsync<string>(db, ApiProposalTests.Alpha, "select canonical_body from proposal where proposal_id = @p", ("p", id));
        var bodySha = await ApiProposalTests.ReaderScalarAsync<string>(db, ApiProposalTests.Alpha, "select body_sha256 from proposal where proposal_id = @p", ("p", id));
        var proposalHash = await ApiProposalTests.ReaderScalarAsync<string>(db, ApiProposalTests.Alpha, "select proposal_hash from proposal where proposal_id = @p", ("p", id));

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(Encoding.UTF8.GetString(Jcs.Canonicalize(doc.RootElement)), canonical);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical!))), bodySha);
        Assert.Equal(Jcs.ProposalHash(doc.RootElement), proposalHash);

        var r = doc.RootElement;
        var four = "{\"action_type\":" + JsonSerializer.Serialize(r.GetProperty("action_type").GetString())
            + ",\"justification\":" + JsonSerializer.Serialize(r.GetProperty("justification").GetString())
            + ",\"parameters\":" + Encoding.UTF8.GetString(Jcs.Canonicalize(r.GetProperty("parameters")))
            + ",\"party_ref\":" + JsonSerializer.Serialize(r.GetProperty("party_ref").GetString()) + "}";
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(four))), proposalHash);
    }

    [Fact]
    public async Task API_07_concurrent_identical_first_submissions_yield_one_201_and_replays()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var body = ApiProposalTests.BodyFor("race-same");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var c = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
            var r = await Post(c, body);
            return (r.StatusCode, Json: await ReadAsync(r));
        }));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, results.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Single(results.Select(x => x.Json.GetProperty("proposal_id").GetGuid()).Distinct());
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Fact]
    public async Task API_07_concurrent_different_bodies_yield_one_201_and_conflicts()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var codes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var c = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
            return (await Post(c, WithJustification("race-diff", "j" + i))).StatusCode;
        }));
        Assert.Single(codes, x => x == HttpStatusCode.Created);
        Assert.Equal(7, codes.Count(x => x == HttpStatusCode.Conflict));
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }
}
