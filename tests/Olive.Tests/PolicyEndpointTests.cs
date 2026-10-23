using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Catalog;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class PolicyEndpointTests(PostgresFixture fixture)
{
    private const string Sentinel = "SENTINEL-PARAM-VALUE-7731";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Env(string action = "crm.case_note@1", string runId = "r", string caseRef = "payload://c1") =>
        "{\"run_id\":\"" + runId + "\",\"action_type\":\"" + action + "\",\"party_ref\":\"party://p1\",\"parameters\":{\"case_ref\":\"" + caseRef
        + "\",\"note_ref\":\"payload://n1\"},\"justification\":\"j\",\"trace_ref\":\"trace://t1\"}";

    // The fixture key's purpose is "servicing"; this catalog only permits "collections".
    private static CatalogSet WrongPurposeCatalog()
    {
        var any = SchemaValidator.Compile("{\"type\":\"object\"}");
        return new([new CatalogRelease(1, [new CatalogEntry("crm.case_note@1", JsonDocument.Parse("{}").RootElement, any, RequiredPurpose: ["collections"])])]);
    }

    private static Task<T?> Scalar<T>(TestDatabase db, string sql, Guid id) =>
        ApiProposalTests.ReaderScalarAsync<T>(db, TestPaths.TenantAlpha, sql, ("p", id));

    private static async Task<(JsonElement Body, Guid Id)> AssertCreatedRefusal(HttpResponseMessage r, string reason)
    {
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        Assert.Equal("refused_policy", body.GetProperty("state").GetString());
        Assert.Equal(reason, body.GetProperty("refusal_reason").GetString());
        return (body, body.GetProperty("proposal_id").GetGuid());
    }

    [Fact]
    public async Task POL_01_unknown_action_is_stored_refused_not_in_catalog()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(action: "crm.unknown@1")), Ct);
        var (_, id) = await AssertCreatedRefusal(r, "not_in_catalog");

        Assert.NotNull(r.Headers.Location);
        Assert.DoesNotContain(factory.OutboundCalls, c => c.Port is nameof(Olive.Application.ITraceResolver) or nameof(Olive.Application.ITokenIssuer));
        Assert.DoesNotContain("token", factory.Events);
        Assert.DoesNotContain("resolve", factory.Events);
        Assert.Equal("policy_evaluated,policy_refused,submitted", await Scalar<string>(db, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", id));
        Assert.Equal("refused_policy", await Scalar<string>(db, "select state from proposal where proposal_id = @p", id));
    }

    [Fact]
    public async Task POL_01_wrong_purpose_is_stored_refused_after_a_verified_trace()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(WrongPurposeCatalog()));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(Env()), Ct);
        var (_, id) = await AssertCreatedRefusal(r, "purpose_not_permitted");

        Assert.Equal(1, factory.Events.Count(e => e == "resolve"));
        Assert.Equal("policy_evaluated,policy_refused,submitted", await Scalar<string>(db, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", id));
        Assert.NotEqual("awaiting_approval", await Scalar<string>(db, "select state from proposal where proposal_id = @p", id));
    }

    [Fact]
    public async Task POL_01_trace_refusal_wins_over_purpose_refusal()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit, configureServices: s => s.AddSingleton(WrongPurposeCatalog()));
        var body = TraceFixtureLoader.Seed(factory, "run-differs");
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        var (_, id) = await AssertCreatedRefusal(r, "trace_unresolved");
        Assert.Equal("submitted,trace_refused", await Scalar<string>(db, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", id));
    }

    [Fact]
    public async Task POL_04_policy_evaluated_detail_is_static_and_has_no_parameter_values()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(WrongPurposeCatalog()));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(caseRef: Sentinel)), Ct);
        var (_, id) = await AssertCreatedRefusal(r, "purpose_not_permitted");

        var detail = await Scalar<string>(db, "select detail::text from proposal_event where proposal_id = @p and event_type = 'policy_evaluated'", id);
        using var doc = JsonDocument.Parse(detail!);
        Assert.Equal("refused", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("purpose_not_permitted", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("checks").GetArrayLength());
        Assert.Equal(0, await Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p and detail::text like '%SENTINEL%'", id));
        Assert.Equal(0, await Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p and detail::text like '%@example%'", id));
        Assert.DoesNotContain(Sentinel, factory.Logs.Text);
    }

    [Theory]
    [InlineData("crm.unknown@1", "not_in_catalog")]
    [InlineData("crm.case_note@1", "purpose_not_permitted")]
    public async Task POL_05_replay_of_policy_refusal_returns_200_same_reason_and_no_new_events(string action, string reason)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(WrongPurposeCatalog()));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var first = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(action: action)), Ct);
        var (_, id) = await AssertCreatedRefusal(first, reason);
        var before = await Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p", id);

        var again = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(action: action)), Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(id, body.GetProperty("proposal_id").GetGuid());
        Assert.Equal(reason, body.GetProperty("refusal_reason").GetString());
        Assert.Equal(before, await Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p", id));

        var different = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(Env(action: action, caseRef: "payload://other")), Ct);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
    }

    [Fact]
    public async Task POL_01_invalid_parameters_for_known_action_is_still_400_and_stores_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = Env().Replace("\"note_ref\":\"payload://n1\"", "\"note_ref\":5");
        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
    }
}
