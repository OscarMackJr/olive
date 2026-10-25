using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class SecretLeakageTests(PostgresFixture fixture)
{
    private sealed class HostileIntrospector(string key) : IKeyIntrospector
    {
        public Task<IntrospectionResult> IntrospectAsync(SessionKey k, CancellationToken ct) =>
            throw new InvalidOperationException("introspection blew up for key " + key);
    }

    // Everything an observer could see: body, all headers and all captured log text.
    private static async Task<string> EverythingAsync(HttpResponseMessage res, OliveApiFactory factory)
    {
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var headers = string.Join("\n", res.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value))
            .Concat(res.Content.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value))));
        return body + "\n" + headers + "\n" + factory.Logs.Text;
    }

    [Fact]
    public async Task FND_06_inactive_key_sentinel_never_appears_anywhere()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        using var client = factory.CreateKeyClient(sentinel);
        using var res = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.DoesNotContain(sentinel, await EverythingAsync(res, factory));
    }

    [Fact]
    public async Task FND_06_successful_post_never_echoes_the_real_key()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var res = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.NotNull(res.Headers.Location);
        Assert.DoesNotContain(ApiProposalTests.AlphaKey, await EverythingAsync(res, factory));
    }

    [Fact]
    public async Task FND_06_hostile_exception_gives_generic_500_without_key_or_stack()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        await using var factory = new OliveApiFactory(db, new HostileIntrospector(sentinel));
        using var client = factory.CreateKeyClient(sentinel);
        using var res = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var all = await EverythingAsync(res, factory);
        Assert.Contains("internal_error", all);
        Assert.DoesNotContain(sentinel, all);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("StackTrace", body);
        Assert.DoesNotContain("at Olive.", body);
        Assert.DoesNotContain("Authorization", body);
    }

    [Fact]
    public async Task FND_06_hostile_exception_logs_contain_no_key_or_message()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        await using var factory = new OliveApiFactory(db, new HostileIntrospector(sentinel));
        using var client = factory.CreateKeyClient(sentinel);
        using var res = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(sentinel, factory.Logs.Text);
        Assert.DoesNotContain("blew up", factory.Logs.Text);
    }

    [Fact]
    public async Task FND_06_structured_logging_of_session_key_is_redacted()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var key = new SessionKey("SENTINEL-" + Guid.NewGuid().ToString("N"));
        factory.Logs.CreateLogger("test").LogInformation("received {Key}", key);
        Assert.Contains("[REDACTED]", factory.Logs.Text);
        Assert.DoesNotContain(key.ExposeSecret(), factory.Logs.Text);
    }

    [Fact]
    public async Task FND_06_unauthenticated_request_returns_no_developer_exception_page()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, new HostileIntrospector("k"));
        using var client = factory.CreateKeyClient("anything");
        using var res = await client.GetAsync("/v1/proposals/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anything", body);
    }

    // IDN-08: the popeye service token and the steward's defer rationale never reach logs, responses or unintended storage.
    [Fact]
    public async Task IDN_08_service_token_and_defer_rationale_never_appear_in_logs_responses_or_unintended_rows()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var token = "SENTINEL-SVC-" + Guid.NewGuid().ToString("N");
        var rationale = "SENTINEL-RATIONALE-" + Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;
        var alphaJson = await File.ReadAllTextAsync(Path.Combine(TestPaths.Root, "contracts", "service-token", "bluto-alpha.json"), ct);
        await using var factory = new OliveApiFactory(db, configureServices: services =>
            services.AddSingleton<IServiceTokenVerifier>(new Olive.Integrations.Popeye.StubServiceTokenVerifier(
                new Dictionary<string, string> { [token] = alphaJson }, TimeProvider.System)));
        using var bluto = factory.CreateServiceClient(token);
        using var stranger = factory.CreateServiceClient(token + "-unknown");
        using var steward = factory.CreateReviewerClient("alpha-steward-1");
        var producerRef = Guid.NewGuid();
        var body = "{\"kind\":\"identity_review\",\"subtype\":\"candidate_pair\",\"producer_ref\":\"" + producerRef + "\",\"subject_ref\":\"bluto:candidate/" + producerRef
            + "\",\"payload_ref\":\"bluto:review-evidence/" + producerRef + "\",\"ruleset_version\":\"rules-1\"}";

        var responses = new List<HttpResponseMessage>();
        var create = await bluto.PostAsync("/v1/queue/items", ApiProposalTests.Json(body), ct);
        responses.Add(create);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var item = System.Text.Json.JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("item_id").GetGuid();
        var refused = await stranger.GetAsync("/v1/queue/items?kind=identity_review&state=decided", ct);
        responses.Add(refused);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await steward.PostAsync($"/v1/queue/items/{item}/claim", null, ct)).StatusCode);
        var defer = await steward.PostAsync($"/v1/queue/items/{item}/decision", ApiProposalTests.Json("{\"decision\":\"defer\",\"rationale\":\"" + rationale + "\"}"), ct);
        responses.Add(defer);
        Assert.Equal(HttpStatusCode.OK, defer.StatusCode);
        responses.Add(await bluto.GetAsync("/v1/queue/items?kind=identity_review&state=decided", ct));
        responses.Add(await bluto.PostAsync($"/v1/queue/items/{item}/withdraw", ApiProposalTests.Json("{\"reason\":\"source_changed\"}"), ct));

        var scan = await SentinelScan.CollectAsync(factory, db, responses.ToArray());
        scan.AssertAbsent(token, rationale);

        // Stored rows: the token appears nowhere; the rationale only in queue_item.rationale and the deferred/decided event detail.
        Assert.Equal(0L, await db.AdminCountAsync($"select count(*) from queue_item t where row_to_json(t)::text like '%{token}%'"));
        Assert.Equal(0L, await db.AdminCountAsync($"select count(*) from queue_item_event t where row_to_json(t)::text like '%{token}%'"));
        Assert.Equal(0L, await db.AdminCountAsync($"select count(*) from queue_item t where (to_jsonb(t) - 'rationale')::text like '%{rationale}%'"));
        Assert.Equal(0L, await db.AdminCountAsync($"select count(*) from queue_item_event t where event_type not in ('deferred', 'decided') and row_to_json(t)::text like '%{rationale}%'"));
    }
}
