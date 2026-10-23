using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
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

    private const string AlphaUser = "analyst.alpha@example.test";
    private const string Entitlement = "ent:crm.case_note.write";

    private static readonly Dictionary<string, string?> ShortTimeout = new() { ["OLIVE_TRACE_TIMEOUT_MS"] = "100" };

    private static async Task<HttpResponseMessage> PostEnv(OliveApiFactory factory, string? body = null)
    {
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        return await client.PostAsync("/v1/proposals", ApiProposalTests.Json(body ?? Env()), Ct);
    }

    [Fact]
    public async Task POL_02_user_without_entitlement_is_stored_entitlement_missing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);

        var r = await PostEnv(factory);
        var (_, id) = await AssertCreatedRefusal(r, "entitlement_missing");

        Assert.Equal("policy_evaluated,policy_refused,submitted", await Scalar<string>(db, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", id));
        Assert.Equal("refused_policy", await Scalar<string>(db, "select state from proposal where proposal_id = @p", id));
        Assert.Single(factory.Directory.Requests);
    }

    [Fact]
    public async Task POL_02_user_holding_only_other_groups_is_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, ["ent:something.else"]);
        await AssertCreatedRefusal(await PostEnv(factory), "entitlement_missing");
    }

    [Fact]
    public async Task POL_02_unknown_user_is_stored_entitlement_missing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, innerDirectory: new Olive.Integrations.Entra.FixtureDirectoryGroupResolver());
        await AssertCreatedRefusal(await PostEnv(factory), "entitlement_missing");
    }

    [Fact]
    public async Task POL_02_agent_without_entitlement_is_refused_even_if_the_user_has_it()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var emptyAllowlist = AgentAllowlist.Parse("agents:\n  - agent_id: liam\n    entitlements: []\n");
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton<IAgentAllowlist>(emptyAllowlist));
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, [Entitlement]);
        await AssertCreatedRefusal(await PostEnv(factory), "entitlement_missing");
    }

    [Fact]
    public async Task POL_02_user_and_agent_both_entitled_is_accepted_and_directory_gets_tenant_and_verbatim_user()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var r = await PostEnv(factory);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("awaiting_approval", JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("state").GetString());
        var call = Assert.Single(factory.Directory.Requests);
        Assert.Equal(TestPaths.TenantAlpha, call.Tenant);
        Assert.Equal(AlphaUser, call.OnBehalfOf);
    }

    [Fact]
    public async Task POL_02_cross_tenant_user_does_not_resolve()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        // The same user name is entitled only under the other tenant: alpha's lookup must not see it.
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);
        factory.FixtureDirectory.Register(TestPaths.TenantBeta, AlphaUser, [Entitlement]);
        await AssertCreatedRefusal(await PostEnv(factory), "entitlement_missing");
    }

    [Theory]
    [InlineData(ResolverFault.Unavailable)]
    [InlineData(ResolverFault.Hang)]
    [InlineData(ResolverFault.Throw)]
    [InlineData(ResolverFault.Malformed)]
    [InlineData(ResolverFault.CallerRejected401)]
    [InlineData(ResolverFault.CallerRejected403)]
    public async Task POL_02_directory_unavailable_timeout_throw_malformed_gives_503_retryable_and_nothing_stored(ResolverFault fault)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var faulting = new FaultingDirectoryResolver(new Olive.Integrations.Entra.FixtureDirectoryGroupResolver(), fault);
        await using var factory = new OliveApiFactory(db, extraConfig: ShortTimeout, innerDirectory: faulting);

        var r = await PostEnv(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync(Ct);
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal("directory_unavailable", json.GetProperty("reason").GetString());
        Assert.True(json.GetProperty("retryable").GetBoolean());
        Assert.Equal(TimeSpan.FromSeconds(5), r.Headers.RetryAfter?.Delta);
        Assert.DoesNotContain("secret-detail", text);
        Assert.DoesNotContain("secret-detail", factory.Logs.Text);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
        Assert.DoesNotContain("insert", factory.Events);
        Assert.DoesNotContain("insert_refused", factory.Events);
    }

    [Theory]
    [InlineData(ResolverFault.CallerRejected401, "directory_resolver_401")]
    [InlineData(ResolverFault.CallerRejected403, "directory_resolver_403")]
    [InlineData(ResolverFault.Malformed, "directory_response_malformed")]
    public async Task POL_02_caller_rejected_and_malformed_raise_the_matching_alarm(ResolverFault fault, string code)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var faulting = new FaultingDirectoryResolver(new Olive.Integrations.Entra.FixtureDirectoryGroupResolver(), fault);
        await using var factory = new OliveApiFactory(db, innerDirectory: faulting);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostEnv(factory)).StatusCode);
        Assert.Contains(code, factory.Alarms.Codes);
    }

    [Fact]
    public async Task POL_02_resolver_not_called_for_refused_purpose_or_not_in_catalog()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var purposeFactory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(WrongPurposeCatalog()));
        await AssertCreatedRefusal(await PostEnv(purposeFactory), "purpose_not_permitted");
        Assert.Empty(purposeFactory.Directory.Requests);
        Assert.DoesNotContain("directory", purposeFactory.Events);

        await using var db2 = await fixture.CreateDatabaseAsync();
        await using var catalogFactory = new OliveApiFactory(db2);
        await AssertCreatedRefusal(await PostEnv(catalogFactory, Env(action: "crm.unknown@1")), "not_in_catalog");
        Assert.Empty(catalogFactory.Directory.Requests);
    }

    [Fact]
    public async Task POL_02_resolver_not_called_when_trace_is_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var body = TraceFixtureLoader.Seed(factory, "run-differs");
        await AssertCreatedRefusal(await PostEnv(factory, body), "trace_unresolved");
        Assert.Empty(factory.Directory.Requests);
    }

    [Fact]
    public async Task POL_02_replay_of_entitlement_refusal_does_not_call_resolver_again()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, []);
        var (_, id) = await AssertCreatedRefusal(await PostEnv(factory), "entitlement_missing");
        var before = await Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p", id);

        var again = await PostEnv(factory);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid());
        Assert.Single(factory.Directory.Requests);
        Assert.Equal(before, await Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p", id));
    }

    [Fact]
    public async Task POL_04_no_group_names_or_on_behalf_of_in_event_detail_or_logs()
    {
        const string group = "ent:SENTINEL-GROUP-5521";
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        factory.FixtureDirectory.Register(TestPaths.TenantAlpha, AlphaUser, [group]);

        var r = await PostEnv(factory);
        var (_, id) = await AssertCreatedRefusal(r, "entitlement_missing");

        var events = await Scalar<string>(db, "select string_agg(detail::text, E'\n') from proposal_event where proposal_id = @p", id) ?? "";
        var scan = new SentinelScan();
        scan.AddSource("event details", events);
        scan.AddSource("logs", factory.Logs.Text);
        await scan.AddResponseAsync("response", r);
        scan.AssertAbsent(group, AlphaUser);
    }

    [Fact]
    public async Task POL_04_directory_outage_logs_carry_no_group_names_or_on_behalf_of()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var faulting = new FaultingDirectoryResolver(new Olive.Integrations.Entra.FixtureDirectoryGroupResolver(), ResolverFault.Throw);
        await using var factory = new OliveApiFactory(db, innerDirectory: faulting);
        await PostEnv(factory);
        Assert.DoesNotContain(AlphaUser, factory.Logs.Text);
        Assert.DoesNotContain(Entitlement, factory.Logs.Text);
    }
}
