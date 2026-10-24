using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Olive.Application;
using Olive.Domain;
using Olive.Host.Alarms;
using Olive.Integrations.Hometown;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

// IDN-08 containment evidence: the port graph (ArchitectureTests), recorded outbound calls, and a sentinel search.
// The sentinel search covers HTTP responses, logs captured at Trace level, stored rows and the JSONL execution output,
// which is added as a source through SentinelScan.AddSource (via AddJsonl).
[Trait("Category", "Integration")]
public sealed class ContainmentTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Sentinels(string Key, string Token)
    {
        public static Sentinels New() => new("SENTINEL-KEY-" + Guid.NewGuid().ToString("N"), "SENTINEL-TOKEN-" + Guid.NewGuid().ToString("N"));

        // Popeye stub that knows the sentinel session key as an active alpha agent key.
        public StubKeyIntrospector Introspector()
        {
            var json = File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "introspection", "liam-alpha.json"));
            return new StubKeyIntrospector(new Dictionary<string, string> { [Key] = json }, TimeProvider.System);
        }
    }

    // Issues a known sentinel token for the grant derived from the introspected key; optionally mutates the grant.
    private sealed class FixedTokenIssuer(IKeyIntrospector introspector, string token, Func<ServiceTokenGrant, ServiceTokenGrant>? mutate = null) : ITokenIssuer
    {
        public async Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct)
        {
            var result = await introspector.IntrospectAsync(key, ct);
            if (!result.Active || result.Key is not { } k) { return new TokenIssueResult.Refused(); }
            var grant = new ServiceTokenGrant(new ServiceToken(token), k.TenantId, k.AgentId!, k.KeyId, DateTimeOffset.UtcNow.AddMinutes(15));
            return new TokenIssueResult.Issued(mutate is null ? grant : mutate(grant));
        }
    }

    private static void RealAlarmLog(IServiceCollection s) =>
        s.AddSingleton<IOperationalAlarm>(sp => new LoggingOperationalAlarm(sp.GetRequiredService<ILoggerFactory>()));

    private static OliveApiFactory Factory(TestDatabase db, Sentinels s, ITraceResolver? resolver = null, Func<ServiceTokenGrant, ServiceTokenGrant>? mutate = null)
    {
        var keys = s.Introspector();
        return new OliveApiFactory(db, introspector: keys, traceMode: TraceMode.Explicit, innerResolver: resolver,
            innerIssuer: new FixedTokenIssuer(keys, s.Token, mutate), configureServices: RealAlarmLog);
    }

    private static FixtureTraceResolver Seeded(string caseName)
    {
        var fx = new FixtureTraceResolver();
        var facts = TraceFixtureLoader.Load(caseName);
        fx.Register(facts.TraceId, facts);
        return fx;
    }

    private static async Task<(HttpResponseMessage Res, SentinelScan Scan)> RunAsync(TestDatabase db, Sentinels s, OliveApiFactory factory, string bodyCase)
    {
        using var client = factory.CreateKeyClient(s.Key);
        var res = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(TraceFixtureLoader.LoadProposalBody(bodyCase)), Ct);
        var scan = await SentinelScan.CollectAsync(factory, db, res);
        return (res, scan);
    }

    [Fact]
    public async Task IDN_08_session_key_reaches_only_popeye_and_service_token_only_hometown()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s);
        TraceFixtureLoader.Seed(factory, "match");
        var (res, _) = await RunAsync(db, s, factory, "match");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var calls = factory.OutboundCalls.ToArray();
        Assert.All(calls.Where(c => c.CarriedSessionKey), c => Assert.Equal(OutboundDestination.Popeye, c.Destination));
        Assert.All(calls.Where(c => c.CarriedServiceToken), c => Assert.Equal(OutboundDestination.Hometown, c.Destination));
        Assert.DoesNotContain(calls, c => c.CarriedSessionKey && c.CarriedServiceToken);
        foreach (var port in new[] { nameof(IKeyIntrospector), nameof(ITokenIssuer), nameof(ITraceResolver) })
        {
            Assert.Contains(calls, c => c.Port == port);
        }

        Assert.Contains(calls, c => c.Port == nameof(ITraceResolver) && c.CarriedServiceToken);
        Assert.Contains(calls, c => c.Port == nameof(ITokenIssuer) && c.CarriedSessionKey);
    }

    [Fact]
    public async Task IDN_08_sentinel_key_and_token_absent_from_responses_logs_and_rows_on_verified_path()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s);
        TraceFixtureLoader.Seed(factory, "match");
        var (res, scan) = await RunAsync(db, s, factory, "match");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Contains(factory.OutboundCalls, c => c.CarriedServiceToken);
        Assert.Contains("rows:proposal", scan.SourceNames);
        scan.AssertAbsent(s.Key, s.Token);
    }

    [Fact]
    public async Task IDN_08_sentinel_key_and_token_absent_on_refused_path()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s);
        TraceFixtureLoader.Seed(factory, "hash-differs");
        var (res, scan) = await RunAsync(db, s, factory, "hash-differs");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Contains("trace_proposal_mismatch", await res.Content.ReadAsStringAsync(Ct));
        scan.AssertAbsent(s.Key, s.Token);
    }

    [Fact]
    public async Task IDN_08_sentinel_key_and_token_absent_on_503_hometown_401_path_including_alarm_log()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s, new FaultingTraceResolver(Seeded("match"), ResolverFault.CallerRejected401));
        var (res, scan) = await RunAsync(db, s, factory, "match");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains(AlarmCodes.TraceResolver401, factory.Logs.Text);
        Assert.True(factory.Logs.CountAt(LogLevel.Critical) > 0);
        scan.AssertAbsent(s.Key, s.Token);
    }

    [Fact]
    public async Task IDN_08_sentinel_key_and_token_absent_on_422_path()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s);
        var (res, scan) = await RunAsync(db, s, factory, "match");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        scan.AssertAbsent(s.Key, s.Token);
    }

    [Fact]
    public async Task IDN_08_sentinel_key_and_token_absent_on_token_claims_mismatch_alarm_path()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s, Seeded("match"),
            g => new ServiceTokenGrant(g.Token, Guid.NewGuid(), g.DelegatedAgentId, g.KeyId, g.ExpiresAt));
        var (res, scan) = await RunAsync(db, s, factory, "match");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains(AlarmCodes.TokenClaimsMismatch, factory.Logs.Text);
        scan.AssertAbsent(s.Key, s.Token);
    }

    [Fact]
    public async Task IDN_08_logging_grant_and_session_key_as_structured_arguments_is_redacted()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s);
        var grant = new ServiceTokenGrant(new ServiceToken(s.Token), Guid.NewGuid(), "liam", "k1", DateTimeOffset.UtcNow);
        factory.Logs.CreateLogger("test").LogInformation("grant {Grant} key {Key} token {Token}", grant, new SessionKey(s.Key), grant.Token);
        Assert.Contains("[REDACTED", factory.Logs.Text);
        Assert.DoesNotContain(s.Key, factory.Logs.Text);
        Assert.DoesNotContain(s.Token, factory.Logs.Text);
    }

    // Submits as the sentinel agent, approves as a dev reviewer with the given rationale, and returns the evidence.
    private static async Task<(HttpResponseMessage Submit, HttpResponseMessage Claim, HttpResponseMessage Decide, Guid Proposal)> ApproveAsync(
        TestDatabase db, Sentinels s, OliveApiFactory factory, string body, string rationale)
    {
        using var agent = factory.CreateKeyClient(s.Key);
        var submit = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        var proposal = JsonDocument.Parse(await submit.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();
        await db.AdminExecAsync($"update proposal set on_behalf_of = 'analyst.alpha@example.test' where proposal_id = '{proposal}'");
        using var rev = factory.CreateReviewerClient("alpha-sup-1");
        var items = await ReviewerTenantTests.ListAsync(rev);
        var item = items.Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid();
        var claim = await rev.PostAsync($"/v1/queue/items/{item}/claim", null, Ct);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var decide = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve", rationale);
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);
        return (submit, claim, decide, proposal);
    }

    [Fact]
    public async Task IDN_08_session_key_token_and_rationale_absent_from_jsonl_logs_and_responses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        var rationale = "SENTINEL-RATIONALE-" + Guid.NewGuid().ToString("N");
        await using var factory = Factory(db, s);
        TraceFixtureLoader.Seed(factory, "match");

        var (submit, claim, decide, proposal) = await ApproveAsync(db, s, factory, TraceFixtureLoader.LoadProposalBody("match"), rationale);

        var scan = await SentinelScan.CollectAsync(factory, db, submit, claim, decide);
        scan.AddJsonl(factory.ExecutorOutputPath);
        Assert.Contains("jsonl", scan.SourceNames);
        Assert.Contains("rows:execution_ledger", scan.SourceNames);
        Assert.True(factory.Logs.CountAt(LogLevel.Trace) > 0);
        scan.AssertAbsent(s.Key, s.Token, rationale);

        // Not vacuous: the JSONL really holds this proposal, and the rationale is stored only in the queue item.
        var jsonl = File.ReadAllText(factory.ExecutorOutputPath);
        Assert.Contains(proposal.ToString(), jsonl);
        Assert.Equal(rationale, await DecisionHttp.Scalar<string>(db, "select rationale from queue_item where proposal_id = @p", ("p", proposal)));
    }

    // The verified-trace fixture pins the proposal hash, so a changed justification cannot ride that path; this test uses the default stub flow.
    [Fact]
    public async Task IDN_08_justification_sentinel_absent_from_jsonl_and_view_row()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var justification = "SENTINEL-JUSTIFICATION-" + Guid.NewGuid().ToString("N");
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor("idn8j").Replace("\"justification\":\"j\"", "\"justification\":\"" + justification + "\"");
        Assert.Contains(justification, body);
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var proposal = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();
        await db.AdminExecAsync($"update proposal set on_behalf_of = 'analyst.alpha@example.test' where proposal_id = '{proposal}'");
        using var rev = factory.CreateReviewerClient("alpha-sup-1");
        var item = (await ReviewerTenantTests.ListAsync(rev)).Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid();
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);

        var jsonl = File.ReadAllText(factory.ExecutorOutputPath);
        Assert.Contains(proposal.ToString(), jsonl);
        Assert.DoesNotContain(justification, jsonl);
        var execution = await DecisionHttp.Scalar<Guid>(db, "select execution_id from execution_ledger where proposal_id = @p and phase = 'started'", ("p", proposal));
        var row = Assert.Single(await ExecutionChainViewTests.QueryChainAsync(db.ReaderConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, execution));
        Assert.DoesNotContain(justification, JsonSerializer.Serialize(row.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString())));
    }

    [Fact]
    public async Task IDN_08_trace_level_logging_is_active_during_the_scan()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var s = Sentinels.New();
        await using var factory = Factory(db, s);
        TraceFixtureLoader.Seed(factory, "match");
        var (res, _) = await RunAsync(db, s, factory, "match");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.True(factory.Logs.CountAt(LogLevel.Trace) > 0);
        Assert.Contains("Trace verified", factory.Logs.Text);
    }
}

public sealed class SentinelScanSelfTests
{
    [Fact]
    public void IDN_08_scan_failure_names_the_source_but_never_echoes_the_sentinel()
    {
        const string sentinel = "SENTINEL-planted-0123456789abcdef";
        var scan = new SentinelScan();
        scan.AddSource("clean", "nothing here");
        scan.AddSource("planted-source", "before " + sentinel + " after");
        var e = Assert.ThrowsAny<Exception>(() => scan.AssertAbsent(sentinel));
        Assert.Contains("planted-source", e.Message);
        Assert.DoesNotContain(sentinel, e.Message);
        Assert.DoesNotContain("0123456789abcdef", e.Message);
    }
}
