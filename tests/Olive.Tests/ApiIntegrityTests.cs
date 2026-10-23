using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Contracts;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ApiIntegrityTests(PostgresFixture fixture)
{
    private const string Secret = "9007199254740993";

    public static TheoryData<string> GateBodies() => new()
    {
        "{\"run_id\":\"r\",\"justification\":\"a\",\"justification\":\"b\"}",
        "{\"run_id\":\"r\",\"justification\":\"a\",\"justificati\\u006fn\":\"b\"}",
        "{\"run_id\":\"r\",\"parameters\":{\"x\":{\"k\":1,\"k\":2}}}",
        "{\"run_id\":\"r\",\"top\":" + Secret + "}",
        "{\"run_id\":\"r\",\"parameters\":{\"n\":333333333.33333329}}",
        "{\"run_id\":\"r\",\"parameters\":{\"\\ud800\":1}}",
        "{\"run_id\":\"r\",\"parameters\":{\"v\":\"\\udc00\"}}",
        "{\"run_id\":\"r\",\"parameters\":{\"v\":\"\\u0000\"}}",
    };

    [Theory]
    [MemberData(nameof(GateBodies))]
    public async Task API_04_gate_failures_give_400_store_nothing_and_do_not_500(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(body), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.InvalidRequest, Secret, "333333333");
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        Assert.DoesNotContain("insert", factory.Events);
        Assert.False(factory.Logs.HasErrorOrAbove);
    }

    private static string Env(string action = "crm.case_note@1", string party = "\"party://p1\"", string parameters = "{\"case_ref\":\"payload://c1\",\"note_ref\":\"payload://n1\"}", string extra = "", string runId = "r") =>
        "{\"run_id\":\"" + runId + "\",\"action_type\":\"" + action + "\",\"party_ref\":" + party + ",\"parameters\":" + parameters
        + ",\"justification\":\"j\",\"trace_ref\":\"trace://t1\"" + extra + "}";

    private async Task<(JsonElement Problem, long Rows)> PostRefusedAsync(string body, string reason, string[]? forbidden = null)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(body), TestContext.Current.CancellationToken);
        var problem = await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.BadRequest, reason, forbidden ?? []);
        var rows = await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha);
        Assert.DoesNotContain("insert", factory.Events);
        return (problem, rows);
    }

    [Fact]
    public async Task API_04_null_optional_field_gives_400_schema_violation_and_stores_nothing()
    {
        var (p, rows) = await PostRefusedAsync(Env(extra: ",\"nono_session_id\":null"), ProblemReasons.SchemaViolation);
        Assert.Equal("/nono_session_id", p.GetProperty("pointer").GetString());
        Assert.Equal("type", p.GetProperty("keyword").GetString());
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task API_04_coercion_needed_value_is_refused_without_echoing_it()
    {
        var (p, rows) = await PostRefusedAsync(Env(party: "5"), ProblemReasons.SchemaViolation);
        Assert.Equal("/party_ref", p.GetProperty("pointer").GetString());
        Assert.Equal(0, rows);
        var (p2, _) = await PostRefusedAsync(Env(party: "\"leaky-secret-text\"").Replace("\"j\"", "7"), ProblemReasons.SchemaViolation, ["leaky-secret-text"]);
        Assert.Equal("/justification", p2.GetProperty("pointer").GetString());
    }

    [Fact]
    public async Task API_04_missing_required_field_gives_400_schema_violation()
    {
        var body = Env().Replace(",\"justification\":\"j\"", "");
        var (p, rows) = await PostRefusedAsync(body, ProblemReasons.SchemaViolation);
        Assert.Equal("required", p.GetProperty("keyword").GetString());
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task API_05_POL_01_unknown_action_type_is_a_stored_201_refusal_not_a_400()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(action: "nope@1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("refused_policy", body.GetProperty("state").GetString());
        Assert.Equal("not_in_catalog", body.GetProperty("refusal_reason").GetString());
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        Assert.False(factory.Logs.HasErrorOrAbove);
    }

    [Fact]
    public async Task API_05_parameter_schema_failures_give_400_with_prefixed_pointer_and_store_nothing()
    {
        var (p, rows) = await PostRefusedAsync(Env(parameters: "{\"case_ref\":\"c\",\"note_ref\":\"n\",\"extra\":1}"), ProblemReasons.SchemaViolation);
        Assert.Equal("/parameters", p.GetProperty("pointer").GetString());
        Assert.Equal("additionalProperties", p.GetProperty("keyword").GetString());
        Assert.Equal(0, rows);
        var (p2, _) = await PostRefusedAsync(Env(parameters: "{\"case_ref\":12,\"note_ref\":\"n\"}"), ProblemReasons.SchemaViolation);
        Assert.Equal("/parameters/case_ref", p2.GetProperty("pointer").GetString());
    }

    [Fact]
    public async Task API_05_gate_failure_wins_over_schema_failure()
    {
        // Duplicate key and schema-invalid at once: the gate answers first.
        var body = "{\"run_id\":\"r\",\"run_id\":\"r2\",\"zzz\":1}";
        await PostRefusedAsync(body, ProblemReasons.InvalidRequest);
    }

    [Fact]
    public async Task API_05_schema_failure_then_corrected_resubmission_under_same_run_id_succeeds()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var bad = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(runId: "same", parameters: "{\"case_ref\":\"c\"}")), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(bad, HttpStatusCode.BadRequest, ProblemReasons.SchemaViolation);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        var good = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(runId: "same")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Fact]
    public async Task API_04_padded_values_are_stored_untouched()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(party: "\"  x  \"")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("  x  ", await ApiProposalTests.ReaderScalarAsync<string>(db, ApiProposalTests.Alpha, "select party_ref from proposal"));
    }

    [Fact]
    public async Task API_04_exact_number_spellings_and_minimal_bodies_still_succeed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(ApiProposalTests.PermissiveCatalog()));
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        // Minimal now means every required field and no optional one.
        var minimal = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.BodyFor("r")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, minimal.StatusCode);
        var numbers = await alpha.PostAsync("/v1/proposals",
            ApiProposalTests.Json("{\"run_id\":\"r2\",\"action_type\":\"test.open@1\",\"party_ref\":\"p\",\"parameters\":{\"a\":1.0,\"b\":0.1,\"c\":1e0},\"justification\":\"j\",\"trace_ref\":\"t\"}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, numbers.StatusCode);
        var full = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, full.StatusCode);
        Assert.False(factory.Logs.HasErrorOrAbove);
    }
}
