using Olive.Application;
using Olive.Domain;
using static Olive.Tests.TraceBuilders;

namespace Olive.Tests;

public sealed class TraceVerifierTests
{
    private static readonly SessionKey Session = new("session-secret");
    private const string Hash = "abc123";

    private sealed record Rig(TraceVerifier Verifier, FakeTokenIssuer Issuer, RecordingAlarm Alarm);

    private static Rig Make(ITraceResolver resolver, TokenIssueResult? token = null, TimeSpan? timeout = null)
    {
        var alarm = new RecordingAlarm();
        var issuer = new FakeTokenIssuer(token ?? Issued());
        var provider = new ServiceTokenProvider(issuer, alarm, new ManualTimeProvider(Start));
        var options = timeout is null ? new TraceVerifierOptions() : new TraceVerifierOptions(timeout.Value);
        return new Rig(new TraceVerifier(provider, resolver, alarm, options), issuer, alarm);
    }

    private static Task<TraceVerdict> Run(Rig rig, string runId = "run-1", string hash = Hash, CancellationToken ct = default) =>
        rig.Verifier.VerifyAsync(Session, Key(), runId, "trace-1", hash, ct);

    private static FakeTraceResolver Resolver(TraceFacts facts) => new(new TraceLookup.Found(facts, TraceVerificationSources.Hometown));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_03_all_four_correct_is_verified_with_source_and_schema()
    {
        var verdict = await Run(Make(Resolver(Facts())), ct: TestContext.Current.CancellationToken);
        var v = Assert.IsType<TraceVerdict.Verified>(verdict);
        Assert.Equal("hometown", v.Source);
        Assert.Equal("ate/0.3", v.SchemaVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_02_not_found_gives_not_found_verdict()
    {
        var rig = Make(new FakeTraceResolver(new TraceLookup.NotFound()));
        Assert.IsType<TraceVerdict.NotFound>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Empty(rig.Alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_03_different_run_is_unresolved_and_carries_source_and_schema()
    {
        var verdict = await Run(Make(Resolver(Facts(runId: "other"))), ct: TestContext.Current.CancellationToken);
        var r = Assert.IsType<TraceVerdict.Refused>(verdict);
        Assert.Equal(TraceRefusalReasons.Unresolved, r.Reason);
        Assert.Equal("hometown", r.Source);
        Assert.Equal("ate/0.3", r.SchemaVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_03_null_run_id_is_unresolved_for_ate_0_3()
    {
        var facts = Facts() with { RunId = null, SchemaVersion = "ate/0.3" };
        var r = Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(facts)), ct: TestContext.Current.CancellationToken));
        Assert.Equal(TraceRefusalReasons.Unresolved, r.Reason);
        Assert.Equal("ate/0.3", r.SchemaVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_04_different_tenant_is_tenant_mismatch()
    {
        var r = Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(Facts(tenant: Guid.NewGuid()))), ct: TestContext.Current.CancellationToken));
        Assert.Equal(TraceRefusalReasons.TenantMismatch, r.Reason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_05_different_agent_is_agent_mismatch()
    {
        var r = Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(Facts(agentId: "mallory"))), ct: TestContext.Current.CancellationToken));
        Assert.Equal(TraceRefusalReasons.AgentMismatch, r.Reason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_05_null_agent_is_agent_mismatch()
    {
        var facts = Facts() with { AgentId = null };
        var r = Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(facts)), ct: TestContext.Current.CancellationToken));
        Assert.Equal(TraceRefusalReasons.AgentMismatch, r.Reason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_05_agent_is_compared_to_the_token_delegated_agent()
    {
        // Introspected agent is liam; popeye's token says delegated agent is "delegate". The trace names "delegate".
        var rig = Make(Resolver(Facts(agentId: "delegate")), Issued(Grant(agentId: "delegate")));
        // Provider rejects this (claims disagree with introspection), so no resolver result is trusted.
        Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Equal([AlarmCodes.TokenClaimsMismatch], rig.Alarm.Codes);
    }

    [Theory]
    [InlineData("abc124")]
    [InlineData("ABC123")]
    [InlineData(null)]
    [Trait("Category", "Unit")]
    public async Task TRC_06_hash_difference_null_or_case_is_proposal_mismatch(string? traceHash)
    {
        var facts = Facts() with { ProposalHash = traceHash };
        var r = Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(facts)), ct: TestContext.Current.CancellationToken));
        Assert.Equal(TraceRefusalReasons.ProposalMismatch, r.Reason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_06_first_failure_wins_when_all_four_differ()
    {
        var facts = Facts(runId: "other", tenant: Guid.NewGuid(), agentId: "mallory", hash: "zzz");
        var r = Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(facts)), ct: TestContext.Current.CancellationToken));
        Assert.Equal(TraceRefusalReasons.Unresolved, r.Reason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_06_check_order_is_run_then_tenant_then_agent_then_hash()
    {
        var t = Facts(tenant: Guid.NewGuid(), agentId: "mallory", hash: "zzz");
        Assert.Equal(TraceRefusalReasons.TenantMismatch, Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(t)), ct: TestContext.Current.CancellationToken)).Reason);
        var a = Facts(agentId: "mallory", hash: "zzz");
        Assert.Equal(TraceRefusalReasons.AgentMismatch, Assert.IsType<TraceVerdict.Refused>(await Run(Make(Resolver(a)), ct: TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_resolver_unavailable_is_unavailable_without_alarm()
    {
        var rig = Make(new FakeTraceResolver(new TraceLookup.Unavailable()));
        Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Empty(rig.Alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_resolver_timeout_is_unavailable_without_alarm()
    {
        var rig = Make(new HangingTraceResolver(), timeout: TimeSpan.FromMilliseconds(50));
        var v = Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Equal("trace_resolver_timeout", v.Cause);
        Assert.Empty(rig.Alarm.Codes);
    }

    [Theory]
    [InlineData(401, AlarmCodes.TraceResolver401)]
    [InlineData(403, AlarmCodes.TraceResolver403)]
    [Trait("Category", "Unit")]
    public async Task TRC_07_caller_rejected_is_unavailable_with_status_alarm(int status, string code)
    {
        var rig = Make(new FakeTraceResolver(new TraceLookup.CallerRejected(status)));
        Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Equal([code], rig.Alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_malformed_is_unavailable_with_malformed_alarm()
    {
        var rig = Make(new FakeTraceResolver(new TraceLookup.Malformed()));
        Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Equal([AlarmCodes.TraceResponseMalformed], rig.Alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_token_failure_is_unavailable_and_resolver_is_never_called()
    {
        var resolver = new FakeTraceResolver(new TraceLookup.Found(Facts(), "hometown"));
        var rig = Make(resolver, new TokenIssueResult.Unavailable());
        Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_token_issuer_hang_times_out_to_unavailable_and_resolver_is_never_called()
    {
        var alarm = new RecordingAlarm();
        var provider = new ServiceTokenProvider(new HangingTokenIssuer(), alarm, new ManualTimeProvider(Start));
        var resolver = new FakeTraceResolver(new TraceLookup.Found(Facts(), "hometown"));
        var verifier = new TraceVerifier(provider, resolver, alarm, new TraceVerifierOptions(TimeSpan.FromMilliseconds(50)));
        var v = Assert.IsType<TraceVerdict.Unavailable>(await verifier.VerifyAsync(Session, Key(), "run-1", "trace-1", Hash, TestContext.Current.CancellationToken));
        Assert.Equal("token_issuer_timeout", v.Cause);
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_resolver_exception_is_unavailable_with_type_name_only_and_no_alarm()
    {
        var rig = Make(new ThrowingTraceResolver());
        var v = Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Equal("trace_resolver_exception", v.Cause);
        Assert.Equal(nameof(IOException), v.ExceptionType);
        Assert.Empty(rig.Alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_token_issuer_exception_is_unavailable_with_type_name_only_and_resolver_is_never_called()
    {
        var alarm = new RecordingAlarm();
        var provider = new ServiceTokenProvider(new ThrowingTokenIssuer(), alarm, new ManualTimeProvider(Start));
        var resolver = new FakeTraceResolver(new TraceLookup.Found(Facts(), "hometown"));
        var verifier = new TraceVerifier(provider, resolver, alarm, new TraceVerifierOptions());
        var v = Assert.IsType<TraceVerdict.Unavailable>(await verifier.VerifyAsync(Session, Key(), "run-1", "trace-1", Hash, TestContext.Current.CancellationToken));
        Assert.Equal("token_issuer_exception", v.Cause);
        Assert.Equal(nameof(InvalidOperationException), v.ExceptionType);
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_03_answer_for_a_different_trace_id_is_unavailable_with_malformed_alarm()
    {
        // Every other fact matches; only the trace the answer is for differs from the trace_ref that was asked for.
        var rig = Make(Resolver(Facts() with { TraceId = "trace-other" }));
        var v = Assert.IsType<TraceVerdict.Unavailable>(await Run(rig, ct: TestContext.Current.CancellationToken));
        Assert.Equal("trace_id_mismatch", v.Cause);
        Assert.Equal([AlarmCodes.TraceResponseMalformed], rig.Alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_07_client_abort_propagates_instead_of_unavailable()
    {
        using var cts = new CancellationTokenSource();
        var rig = Make(new HangingTraceResolver());
        var task = Run(rig, ct: cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
