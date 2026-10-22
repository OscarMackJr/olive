using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class TraceRefusalEndpointTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Post(HttpClient c, string body) => c.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);

    private static Task<T?> Scalar<T>(TestDatabase db, string sql, Guid id) =>
        ApiProposalTests.ReaderScalarAsync<T>(db, TestPaths.TenantAlpha, sql, ("p", id));

    // Posts the case body and asserts a stored refusal: 201, Location, body shape, row and events.
    private async Task AssertRefusal(string caseName, string reason, string schemaVersion = "ate/0.4")
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var body = TraceFixtureLoader.Seed(factory, caseName);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var post = await Post(client, body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.NotNull(post.Headers.Location);
        var text = await post.Content.ReadAsStringAsync(Ct);
        var created = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["proposal_id", "refusal_reason", "state"], created.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("refused_policy", created.GetProperty("state").GetString());
        Assert.Equal(reason, created.GetProperty("refusal_reason").GetString());
        var id = created.GetProperty("proposal_id").GetGuid();

        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
        Assert.Equal("refused_policy", await Scalar<string>(db, "select state from proposal where proposal_id = @p", id));
        Assert.Equal(reason, await Scalar<string>(db, "select refusal_reason from proposal where proposal_id = @p", id));
        Assert.Equal("fixture", await Scalar<string>(db, "select trace_verification from proposal where proposal_id = @p", id));
        Assert.Equal(schemaVersion, await Scalar<string>(db, "select trace_schema_version from proposal where proposal_id = @p", id));
        Assert.Equal("submitted,trace_refused", await Scalar<string>(db, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", id));
    }

    [Fact]
    public async Task TRC_03_null_run_id_on_ate_0_3_is_stored_as_trace_unresolved() =>
        await AssertRefusal("run-null-ate03", "trace_unresolved", "ate/0.3");

    [Fact]
    public async Task TRC_03_different_run_id_is_trace_unresolved() =>
        await AssertRefusal("run-differs", "trace_unresolved");

    [Fact]
    public async Task TRC_04_tenant_mismatch_is_trace_tenant_mismatch() =>
        await AssertRefusal("tenant-differs", "trace_tenant_mismatch");

    [Fact]
    public async Task TRC_05_null_agent_is_trace_agent_mismatch() =>
        await AssertRefusal("agent-null", "trace_agent_mismatch");

    [Fact]
    public async Task TRC_05_agent_differing_from_token_delegated_agent_is_trace_agent_mismatch() =>
        await AssertRefusal("agent-differs", "trace_agent_mismatch");

    [Fact]
    public async Task TRC_06_hash_null_is_trace_proposal_mismatch() =>
        await AssertRefusal("hash-null", "trace_proposal_mismatch");

    [Fact]
    public async Task TRC_06_hash_differing_is_trace_proposal_mismatch() =>
        await AssertRefusal("hash-differs", "trace_proposal_mismatch");

    [Fact]
    public async Task TRC_06_one_character_changed_in_justification_after_tracing_is_trace_proposal_mismatch()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var match = TraceFixtureLoader.Seed(factory, "match");
        var altered = match.Replace("\"justification\": \"j\"", "\"justification\": \"k\"");
        Assert.NotEqual(match, altered);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var post = await Post(client, altered);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("trace_proposal_mismatch", created.GetProperty("refusal_reason").GetString());
    }

    [Fact]
    public async Task TRC_06_unknown_key_inside_parameters_is_rejected_before_trace_step()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var match = TraceFixtureLoader.Seed(factory, "match");
        var extra = match.Replace("\"note_ref\": \"payload://n1\"", "\"note_ref\": \"payload://n1\", \"extra\": \"x\"");
        Assert.NotEqual(match, extra);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var post = await Post(client, extra);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
        Assert.DoesNotContain("token", factory.Events);
        Assert.DoesNotContain("resolve", factory.Events);
    }

    [Fact]
    public async Task TRC_03_check_order_run_before_tenant_before_agent_before_hash()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var otherTenant = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");
        const string badHash = "0000000000000000000000000000000000000000000000000000000000000001";
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var now = DateTimeOffset.UtcNow;

        async Task<string?> Step(string suffix, Func<string, string, TraceFacts> facts)
        {
            var node = JsonNode.Parse(TraceFixtureLoader.LoadProposalBody("match"))!.AsObject();
            var run = "run-" + suffix;
            var trace = "trace://t-" + suffix;
            node["run_id"] = run;
            node["trace_ref"] = trace;
            factory.FixtureResolver.Register(trace, facts(run, trace));
            var post = await Post(client, node.ToJsonString());
            Assert.Equal(HttpStatusCode.Created, post.StatusCode);
            return JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("refusal_reason").GetString();
        }

        Assert.Equal("trace_unresolved", await Step("o1", (r, t) => new TraceFacts(t, "ate/0.4", "other-run", otherTenant, "other", badHash, now)));
        Assert.Equal("trace_tenant_mismatch", await Step("o2", (r, t) => new TraceFacts(t, "ate/0.4", r, otherTenant, "other", badHash, now)));
        Assert.Equal("trace_agent_mismatch", await Step("o3", (r, t) => new TraceFacts(t, "ate/0.4", r, TestPaths.TenantAlpha, "other", badHash, now)));
        Assert.Equal("trace_proposal_mismatch", await Step("o4", (r, t) => new TraceFacts(t, "ate/0.4", r, TestPaths.TenantAlpha, "liam", badHash, now)));
    }

    [Fact]
    public async Task TRC_03_replay_of_refused_run_returns_200_same_body_without_token_or_resolver()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var body = TraceFixtureLoader.Seed(factory, "hash-differs");
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var first = await Post(client, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstText = await first.Content.ReadAsStringAsync(Ct);

        // A matching fixture now would verify the body; a refusal is final for (agent_id, run_id).
        using var doc = JsonDocument.Parse(body);
        var good = TraceFixtureLoader.Load("hash-differs") with { ProposalHash = Jcs.ProposalHash(doc.RootElement) };
        factory.FixtureResolver.Register(good.TraceId, good);

        factory.Events.Clear();
        var second = await Post(client, body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstText, await second.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain("token", factory.Events);
        Assert.DoesNotContain("resolve", factory.Events);
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
    }

    [Fact]
    public async Task TRC_03_refused_proposal_is_readable_via_get_with_reason_and_trace_fields()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var body = TraceFixtureLoader.Seed(factory, "tenant-differs");
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var id = JsonDocument.Parse(await (await Post(client, body)).Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();

        var get = await client.GetAsync($"/v1/proposals/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("refused_policy", json.GetProperty("state").GetString());
        Assert.Equal("trace_tenant_mismatch", json.GetProperty("refusal_reason").GetString());
        Assert.Equal("fixture", json.GetProperty("trace_verification").GetString());
        Assert.Equal("ate/0.4", json.GetProperty("trace_schema_version").GetString());
    }

    [Fact]
    public async Task TRC_03_cross_tenant_get_of_refusal_is_404()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var body = TraceFixtureLoader.Seed(factory, "agent-null");
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var beta = factory.CreateKeyClient(ApiProposalTests.BetaKey);
        var id = JsonDocument.Parse(await (await Post(alpha, body)).Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"/v1/proposals/{id}", Ct)).StatusCode);
    }
}
