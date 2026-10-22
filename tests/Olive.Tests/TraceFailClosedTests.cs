using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olive.Application;
using Olive.Integrations.Hometown;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class TraceFailClosedTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Body => TraceFixtureLoader.LoadProposalBody("match");

    private static Task<HttpResponseMessage> Post(HttpClient c, string body) => c.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);

    private static readonly Dictionary<string, string?> ShortTimeout = new() { ["OLIVE_TRACE_TIMEOUT_MS"] = "100" };

    private static FixtureTraceResolver SeededFixture(string caseName = "match")
    {
        var fx = new FixtureTraceResolver();
        var facts = TraceFixtureLoader.Load(caseName);
        fx.Register(facts.TraceId, facts);
        return fx;
    }

    private static OliveApiFactory Factory(TestDatabase db, ITraceResolver? resolver = null, ITokenIssuer? issuer = null, IDictionary<string, string?>? config = null, IKeyIntrospector? introspector = null) =>
        new(db, introspector: introspector, extraConfig: config, traceMode: TraceMode.Explicit, innerResolver: resolver, innerIssuer: issuer);

    // The shared fail-closed contract: 503, retryable, Retry-After 5, nothing stored, no cause in body or headers.
    private static async Task AssertUnavailable(HttpResponseMessage r, TestDatabase db, OliveApiFactory factory)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("trace_verification_unavailable", json.GetProperty("reason").GetString());
        Assert.True(json.GetProperty("retryable").GetBoolean());
        Assert.Equal(TimeSpan.FromSeconds(5), r.Headers.RetryAfter?.Delta);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
        Assert.DoesNotContain("insert", factory.Events);
        Assert.DoesNotContain("insert_refused", factory.Events);
    }

    [Fact]
    public async Task TRC_02_unknown_trace_gives_422_retryable_and_stores_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await Post(client, Body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("trace_not_found", json.GetProperty("reason").GetString());
        Assert.True(json.GetProperty("retryable").GetBoolean());
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
        Assert.DoesNotContain("insert", factory.Events);
        Assert.Empty(factory.Alarms.Codes);
    }

    [Fact]
    public async Task TRC_02_retry_with_same_run_id_after_fixture_appears_is_accepted_because_not_found_is_never_cached()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(client, Body)).StatusCode);

        var facts = TraceFixtureLoader.Load("match");
        factory.FixtureResolver.Register(facts.TraceId, facts);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, Body)).StatusCode);
        Assert.Equal(2, factory.Events.Count(e => e == "resolve"));
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
    }

    [Fact]
    public async Task TRC_07_resolver_unavailable_gives_503_without_alarm()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), ResolverFault.Unavailable));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Empty(factory.Alarms.Codes);
    }

    [Fact]
    public async Task TRC_07_resolver_hang_times_out_to_503_without_alarm()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), ResolverFault.Hang), config: ShortTimeout);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Empty(factory.Alarms.Codes);
    }

    [Fact]
    public async Task TRC_07_popeye_token_hang_times_out_to_503_without_alarm()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture(), new FaultingTokenIssuer(TokenFault.Hang), config: ShortTimeout);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.DoesNotContain("resolve", factory.Events);
        Assert.Empty(factory.Alarms.Codes);
    }

    [Fact]
    public async Task TRC_07_resolver_exception_gives_503_retryable_and_stores_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), ResolverFault.Throw));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Empty(factory.Alarms.Codes);
        Assert.Contains("exception_type=HttpRequestException", factory.Logs.Text);
        Assert.DoesNotContain("resolver-fault-secret-detail", factory.Logs.Text);
        Assert.False(factory.Logs.HasErrorOrAbove);
    }

    [Fact]
    public async Task TRC_07_popeye_token_exception_gives_503_retryable_and_stores_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture(), new FaultingTokenIssuer(TokenFault.Throw));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.DoesNotContain("resolve", factory.Events);
        Assert.Empty(factory.Alarms.Codes);
        Assert.Contains("exception_type=HttpRequestException", factory.Logs.Text);
        Assert.DoesNotContain("token-fault-secret-detail", factory.Logs.Text);
        Assert.False(factory.Logs.HasErrorOrAbove);
    }

    [Fact]
    public async Task TRC_07_hometown_401_gives_503_and_alarm_trace_resolver_401()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), ResolverFault.CallerRejected401));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Equal([AlarmCodes.TraceResolver401], factory.Alarms.Codes);
    }

    [Fact]
    public async Task TRC_07_hometown_403_gives_503_and_alarm_trace_resolver_403()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), ResolverFault.CallerRejected403));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Equal([AlarmCodes.TraceResolver403], factory.Alarms.Codes);
        Assert.NotEqual(AlarmCodes.TraceResolver401, AlarmCodes.TraceResolver403);
    }

    [Fact]
    public async Task TRC_07_malformed_resolver_response_gives_503_and_alarm()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), ResolverFault.Malformed));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Equal([AlarmCodes.TraceResponseMalformed], factory.Alarms.Codes);
    }

    [Theory]
    [InlineData(TokenFault.CallerRejected401, AlarmCodes.TokenIssuer401)]
    [InlineData(TokenFault.CallerRejected403, AlarmCodes.TokenIssuer403)]
    public async Task TRC_07_popeye_token_401_and_403_give_503_and_distinct_alarms(TokenFault fault, string alarm)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture(), new FaultingTokenIssuer(fault));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Equal([alarm], factory.Alarms.Codes);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task TRC_07_introspection_caller_rejected_gives_503_introspection_unavailable_and_alarm(int status)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, introspector: new FaultingKeyIntrospector(status));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await Post(client, Body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("introspection_unavailable", json.GetProperty("reason").GetString());
        Assert.Equal([AlarmCodes.IntrospectionCallerRejected], factory.Alarms.Codes);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, TestPaths.TenantAlpha));
    }

    [Fact]
    public async Task TRC_07_inactive_key_stays_401_and_plain_introspection_outage_stays_503_without_alarm()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using (var factory = Factory(db))
        {
            using var client = factory.CreateKeyClient("no-such-key");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, Body)).StatusCode);
            Assert.Empty(factory.Alarms.Codes);
        }

        await using (var factory = Factory(db, introspector: new ThrowingKeyIntrospector()))
        {
            using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client, Body)).StatusCode);
            Assert.Empty(factory.Alarms.Codes);
        }
    }

    [Theory]
    [InlineData(TokenFault.WrongTenant)]
    [InlineData(TokenFault.WrongDelegatedAgent)]
    [InlineData(TokenFault.WrongKeyId)]
    public async Task IDN_07_token_with_wrong_tenant_delegated_agent_or_key_id_gives_503_and_alarm_token_claims_mismatch(TokenFault fault)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture(), new FaultingTokenIssuer(fault));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Equal([AlarmCodes.TokenClaimsMismatch], factory.Alarms.Codes);
        Assert.DoesNotContain("resolve", factory.Events);
    }

    [Fact]
    public async Task IDN_06_token_expiring_after_session_key_gives_503_and_alarm_token_outlives_session_key_and_is_not_cached()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture(), new FaultingTokenIssuer(TokenFault.ExpiresAfterKey));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Equal([AlarmCodes.TokenOutlivesSessionKey, AlarmCodes.TokenOutlivesSessionKey], factory.Alarms.Codes);
        Assert.Equal(2, factory.Events.Count(e => e == "token"));
        Assert.DoesNotContain("resolve", factory.Events);
    }

    [Fact]
    public async Task IDN_07_token_refused_scope_gives_503_and_alarm()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture(), new FaultingTokenIssuer(TokenFault.Refused));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Equal([AlarmCodes.TokenIssuerRefusedScope], factory.Alarms.Codes);
    }

    [Fact]
    public async Task TRC_08_second_submission_for_same_trace_is_served_from_cache()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture());
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, Body)).StatusCode);

        var node = JsonNode.Parse(Body)!.AsObject();
        node["run_id"] = "run-second";
        var second = await Post(client, node.ToJsonString());
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("trace_unresolved", json.GetProperty("refusal_reason").GetString());
        Assert.Equal(1, factory.Events.Count(e => e == "resolve"));
    }

    [Fact]
    public async Task TRC_08_503_and_401_are_never_cached()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var faulty = new FaultingTraceResolver(SeededFixture(), ResolverFault.Unavailable);
        await using var factory = Factory(db, faulty);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client, Body)).StatusCode);
        faulty.Mode = ResolverFault.CallerRejected401;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client, Body)).StatusCode);
        faulty.Mode = ResolverFault.CallerRejected403;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client, Body)).StatusCode);
        faulty.Mode = ResolverFault.None;
        Assert.Equal(HttpStatusCode.Created, (await Post(client, Body)).StatusCode);
        Assert.Equal(4, factory.Events.Count(e => e == "resolve"));
    }

    [Theory]
    [InlineData(ResolverFault.Hang)]
    [InlineData(ResolverFault.CallerRejected401)]
    [InlineData(ResolverFault.CallerRejected403)]
    [InlineData(ResolverFault.Malformed)]
    [InlineData(ResolverFault.Unavailable)]
    [InlineData(ResolverFault.Throw)]
    public async Task TRC_07_503_body_and_headers_never_expose_the_cause(ResolverFault fault)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), fault), config: ShortTimeout);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await Post(client, Body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var body = (await r.Content.ReadAsStringAsync(Ct)).ToLowerInvariant();
        foreach (var word in new[] { "hang", "timeout", "timed out", "401", "403", "forbidden", "unauthorized", "token", "malformed", "rejected", "popeye", "hometown", "alarm" })
        {
            Assert.DoesNotContain(word, body);
        }

        var headers = string.Join("\n", r.Headers.Concat(r.Content.Headers)
            .Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Key + ": " + string.Join(",", h.Value))).ToLowerInvariant();
        foreach (var word in new[] { "hang", "timeout", "401", "403", "forbidden", "unauthorized", "token", "malformed", "rejected" })
        {
            Assert.DoesNotContain(word, headers);
        }
    }

    [Fact]
    public async Task TRC_07_503_carries_retryable_true_and_retry_after_5()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, new FaultingTraceResolver(SeededFixture(), ResolverFault.Unavailable));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await Post(client, Body);
        Assert.Equal("5", string.Join(",", r.Headers.GetValues("Retry-After")));
        Assert.True(JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task TRC_ORDER_token_failure_means_resolver_is_never_called()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, SeededFixture(), new FaultingTokenIssuer(TokenFault.Unavailable));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await AssertUnavailable(await Post(client, Body), db, factory);
        Assert.Contains("token", factory.Events);
        Assert.DoesNotContain("resolve", factory.Events);
        Assert.Empty(factory.Alarms.Codes);
    }
}
