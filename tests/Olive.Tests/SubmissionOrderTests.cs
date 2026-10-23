using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Olive.Tests;

// API-08 / POL-05: the fixed submission order is proven by peeling one fault at a time from a multi-fault request, with the recorded
// port markers showing that no later step ran. Markers in use: introspect, FindByRun, token, resolve, directory, insert (accepted or
// rate-limited evaluated insert), insert_refused. The schema/catalog validator is not decorated (no production edits to emit markers),
// so the validate step is proven by status plus the absence of every later marker and of any stored row (FALLBACK RULE).
[Trait("Category", "Integration")]
public sealed class SubmissionOrderTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Alpha = ApiProposalTests.Alpha;
    private const string AlphaUser = "analyst.alpha@example.test";

    private static string Body(string runId, bool schemaFault = false, string action = "crm.case_note@1") =>
        ApiProposalTests.BodyFor(runId).Replace("crm.case_note@1", action)
            .Replace("\"note_ref\":\"payload://n1\"", schemaFault ? "\"note_ref\":5" : "\"note_ref\":\"payload://n1\"");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string body)
    {
        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        var text = await r.Content.ReadAsStringAsync(Ct);
        return (r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Reason(JsonElement body) =>
        body.TryGetProperty("reason", out var r) ? r.GetString()! : body.GetProperty("refusal_reason").GetString()!;

    private static async Task<long> Rows(TestDatabase db) => await ApiProposalTests.CountRowsAsync(db, Alpha);

    private static string[] Snapshot(OliveApiFactory f) { lock (f.Events) { return [.. f.Events]; } }

    private static void Reset(OliveApiFactory f) { lock (f.Events) { f.Events.Clear(); } lock (f.OutboundCalls) { f.OutboundCalls.Clear(); } }

    [Fact]
    public async Task API_08_step1_inactive_key_with_every_other_fault_gets_401_and_only_introspect_marker_recorded()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        using var client = factory.CreateKeyClient("fixture-key-inactive");

        var (status, _) = await PostAsync(client, Body("r1", schemaFault: true, action: "crm.unknown@1"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(["introspect"], Snapshot(factory));
        Assert.Equal(0, await Rows(db));
    }

    [Fact]
    public async Task API_08_step2_user_key_with_bad_body_gets_403_principal_not_permitted_before_any_body_dependent_step()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        using var client = factory.CreateKeyClient("fixture-key-user");

        var (status, body) = await PostAsync(client, "{not json");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("principal_not_permitted", Reason(body));
        Assert.Equal(["introspect"], Snapshot(factory));
        Assert.Equal(0, await Rows(db));
    }

    [Fact]
    public async Task API_08_step3_conflicting_run_with_schema_fault_gets_409_idempotency_wins_over_schema()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var (first, _) = await PostAsync(client, Body("same-run"));
        Assert.Equal(HttpStatusCode.Created, first);
        Reset(factory);

        var (status, body) = await PostAsync(client, Body("same-run", schemaFault: true));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("idempotency_conflict", Reason(body));
        Assert.Equal(["introspect", "FindByRun"], Snapshot(factory));
        Assert.Equal(1, await Rows(db));
    }

    [Fact]
    public async Task API_08_step4_schema_fault_with_unknown_trace_gets_400_and_no_trace_marker()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(10, 3600, "[collections]")));
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var (status, body) = await PostAsync(client, Body("r4", schemaFault: true));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("schema_violation", Reason(body));
        Assert.Equal(["introspect", "FindByRun"], Snapshot(factory));
        Assert.DoesNotContain(factory.OutboundCalls, c => c.Port is nameof(Olive.Application.ITraceResolver) or nameof(Olive.Application.ITokenIssuer) or nameof(Olive.Application.IDirectoryGroupResolver));
        Assert.Empty(factory.Directory.Requests);
        Assert.Equal(0, await Rows(db));
    }

    [Fact]
    public async Task API_08_step5_unknown_trace_with_wrong_purpose_gets_422_trace_not_found_and_nothing_stored()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(10, 3600, "[collections]")));
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var (status, body) = await PostAsync(client, Body("r5"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("trace_not_found", Reason(body));
        var events = Snapshot(factory);
        Assert.Contains("resolve", events);
        Assert.DoesNotContain("directory", events);
        Assert.DoesNotContain("insert", events);
        Assert.DoesNotContain("insert_refused", events);
        Assert.Empty(factory.Directory.Requests);
        Assert.Equal(0, await Rows(db));
    }

    [Fact]
    public async Task API_08_step6_trace_ok_wrong_purpose_and_unentitled_stores_purpose_not_permitted_with_no_directory_marker()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(10, 3600, "[collections]")));
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var (status, body) = await PostAsync(client, Body("r6"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("purpose_not_permitted", Reason(body));
        Assert.Equal(["introspect", "FindByRun", "token", "resolve", "insert_refused"], Snapshot(factory));
        Assert.Empty(factory.Directory.Requests);
        Assert.Equal(1, await Rows(db));
    }

    [Fact]
    public async Task API_08_step7_purpose_ok_unentitled_and_over_rate_stores_entitlement_missing_with_directory_once_and_no_rate_lock()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(1, 3600)));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        // The service token is cached from the first call, so no token marker appears on the second. Consume the whole rate budget while entitled, then lose the entitlement so both rate and entitlement would fail.
        var (first, _) = await PostAsync(client, Body("budget"));
        Assert.Equal(HttpStatusCode.Created, first);
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);
        Reset(factory);

        var (status, body) = await PostAsync(client, Body("r7"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("entitlement_missing", Reason(body));
        Assert.Equal(["introspect", "FindByRun", "resolve", "directory", "insert_refused"], Snapshot(factory));
        Assert.Equal(2, factory.Directory.Requests.Count);
        Assert.DoesNotContain("insert", Snapshot(factory));
    }

    [Fact]
    public async Task API_08_step8_all_checks_pass_but_over_limit_stores_rate_limited_and_a_clean_request_ends_awaiting_approval()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(1, 3600)));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var (clean, cleanBody) = await PostAsync(client, Body("clean"));
        Assert.Equal(HttpStatusCode.Created, clean);
        Assert.Equal("awaiting_approval", cleanBody.GetProperty("state").GetString());
        Reset(factory);

        var (status, body) = await PostAsync(client, Body("over"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("refused_policy", body.GetProperty("state").GetString());
        Assert.Equal("rate_limited", Reason(body));
        Assert.Equal(["introspect", "FindByRun", "resolve", "directory", "insert"], Snapshot(factory));
    }

    [Fact]
    public async Task API_08_markers_appear_in_documented_order_for_a_fully_valid_request()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var (status, _) = await PostAsync(client, Body("ordered"));

        Assert.Equal(HttpStatusCode.Created, status);
        // introspect, find_by_run, (validate: undecorated), trace, directory, insert_evaluated
        Assert.Equal(["introspect", "FindByRun", "token", "resolve", "directory", "insert"], Snapshot(factory));
    }

    [Theory]
    [InlineData("not_in_catalog")]
    [InlineData("purpose_not_permitted")]
    [InlineData("entitlement_missing")]
    [InlineData("rate_limited")]
    public async Task POL_05_replay_matrix_returns_200_same_refusal_without_resolver_directory_or_store_insert_calls(string reason)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = reason switch
        {
            "purpose_not_permitted" => new OliveApiFactory(db, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(10, 3600, "[collections]"))),
            "rate_limited" => new OliveApiFactory(db, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(1, 3600))),
            _ => new OliveApiFactory(db),
        };
        if (reason == "entitlement_missing")
        {
            factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);
        }

        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        if (reason == "rate_limited")
        {
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, Body("budget"))).Status);
        }

        var body = Body("replayed", action: reason == "not_in_catalog" ? "crm.unknown@1" : "crm.case_note@1");
        var (first, firstBody) = await PostAsync(client, body);
        Assert.Equal(HttpStatusCode.Created, first);
        Assert.Equal("refused_policy", firstBody.GetProperty("state").GetString());
        Assert.Equal(reason, Reason(firstBody));
        var id = firstBody.GetProperty("proposal_id").GetGuid();
        var eventsBefore = await ApiProposalTests.ReaderScalarAsync<long>(db, Alpha, "select count(*) from proposal_event where proposal_id = @p", ("p", id));
        var rowsBefore = await Rows(db);
        var directoryBefore = factory.Directory.Requests.Count;
        Reset(factory);

        var (replay, replayBody) = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, replay);
        Assert.Equal(id, replayBody.GetProperty("proposal_id").GetGuid());
        Assert.Equal("refused_policy", replayBody.GetProperty("state").GetString());
        Assert.Equal(reason, Reason(replayBody));
        Assert.Equal(["introspect", "FindByRun"], Snapshot(factory));
        Assert.Equal(directoryBefore, factory.Directory.Requests.Count);
        Assert.Equal(eventsBefore, await ApiProposalTests.ReaderScalarAsync<long>(db, Alpha, "select count(*) from proposal_event where proposal_id = @p", ("p", id)));
        Assert.Equal(rowsBefore, await Rows(db));
    }
}
