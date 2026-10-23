using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olive.Application;
using Olive.Domain;
using Olive.Integrations.Hometown;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class TraceVerifiedEndpointTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Dir => Path.Combine(TestPaths.Root, "contracts", "trace-resolution");

    private static string MatchProposal => File.ReadAllText(Path.Combine(Dir, "match.proposal.json"));

    private static void SeedMatch(OliveApiFactory factory)
    {
        var found = Assert.IsType<TraceLookup.Found>(TraceResolutionResponse.Parse(File.ReadAllText(Path.Combine(Dir, "match.json"))));
        factory.FixtureResolver.Register("trace://fixture/match", found.Facts);
    }

    // A valid body for a fresh run and trace ref, with a matching fixture registered for it.
    private static string SeedFor(OliveApiFactory factory, string suffix)
    {
        var node = JsonNode.Parse(MatchProposal)!.AsObject();
        node["run_id"] = "run-" + suffix;
        node["trace_ref"] = "trace://t-" + suffix;
        var body = node.ToJsonString();
        using var doc = JsonDocument.Parse(body);
        factory.FixtureResolver.Register("trace://t-" + suffix,
            new TraceFacts("trace://t-" + suffix, "ate/0.4", "run-" + suffix, TestPaths.TenantAlpha, "liam", Jcs.ProposalHash(doc.RootElement), DateTimeOffset.UtcNow));
        return body;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string body) => c.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);

    [Fact]
    public async Task TRC_09_matching_fixture_trace_is_accepted_and_records_fixture_source()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        SeedMatch(factory);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var post = await Post(client, MatchProposal);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(["proposal_id", "state"], created.EnumerateObject().Select(p => p.Name).ToArray());
        var id = created.GetProperty("proposal_id").GetGuid();

        Assert.Equal("fixture", await ApiProposalTests.ReaderScalarAsync<string>(db, TestPaths.TenantAlpha, "select trace_verification from proposal where proposal_id = @p", ("p", id)));
        Assert.Equal("ate/0.4", await ApiProposalTests.ReaderScalarAsync<string>(db, TestPaths.TenantAlpha, "select trace_schema_version from proposal where proposal_id = @p", ("p", id)));

        var get = await client.GetAsync($"/v1/proposals/{id}", Ct);
        var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("fixture", body.GetProperty("trace_verification").GetString());
        Assert.Equal("ate/0.4", body.GetProperty("trace_schema_version").GetString());
        Assert.False(body.TryGetProperty("refusal_reason", out _));
    }

    [Fact]
    public async Task TRC_06_key_reordered_parameters_still_match()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        SeedMatch(factory);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var reordered = MatchProposal.Replace("\"case_ref\": \"payload://c1\",", "").Replace("\"note_ref\": \"payload://n1\"", "\"note_ref\": \"payload://n1\", \"case_ref\": \"payload://c1\"");
        Assert.NotEqual(MatchProposal, reordered);
        Assert.True(reordered.IndexOf("note_ref", StringComparison.Ordinal) < reordered.IndexOf("case_ref", StringComparison.Ordinal));

        var post = await Post(client, reordered);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
    }

    [Fact]
    public async Task TRC_ORDER_events_show_introspect_then_idempotency_then_token_then_resolve_then_directory_then_insert()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        SeedMatch(factory);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, MatchProposal)).StatusCode);
        Assert.Equal(["introspect", "FindByRun", "token", "resolve", "directory", "insert"], factory.Events);
    }

    [Fact]
    public async Task TRC_ORDER_replay_and_conflict_never_call_token_or_resolver()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        SeedMatch(factory);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, MatchProposal)).StatusCode);

        factory.Events.Clear();
        Assert.Equal(HttpStatusCode.OK, (await Post(client, MatchProposal)).StatusCode);
        var conflicting = MatchProposal.Replace("\"justification\": \"j\"", "\"justification\": \"other\"");
        Assert.NotEqual(MatchProposal, conflicting);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, conflicting)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "{\"run_id\":\"run-bad\"}")).StatusCode);
        Assert.DoesNotContain("token", factory.Events);
        Assert.DoesNotContain("resolve", factory.Events);
        Assert.DoesNotContain("insert", factory.Events);
    }

    [Fact]
    public async Task IDN_05_second_request_reuses_cached_service_token()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var first = SeedFor(factory, "one");
        var second = SeedFor(factory, "two");
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, first)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, second)).StatusCode);
        Assert.Single(factory.Events, e => e == "token");
        Assert.Equal(2, factory.Events.Count(e => e == "resolve"));
    }

    [Fact]
    public async Task TRC_09_auto_fixture_mode_keeps_legacy_bodies_accepted()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, ApiProposalTests.FullBody)).StatusCode);
    }

    [Fact]
    public async Task TRC_09_unknown_trace_gives_422_and_stores_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await Post(client, MatchProposal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
        Assert.DoesNotContain("insert", factory.Events);
    }
}
