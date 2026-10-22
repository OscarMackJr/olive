using Olive.Application;
using Olive.Domain;
using static Olive.Tests.TraceBuilders;

namespace Olive.Tests;

public sealed class ServiceTokenProviderTests
{
    private static readonly SessionKey Session = new("session-secret");

    private static (ServiceTokenProvider Provider, FakeTokenIssuer Issuer, RecordingAlarm Alarm, ManualTimeProvider Time) Make(params TokenIssueResult[] results)
    {
        var issuer = new FakeTokenIssuer(results);
        var alarm = new RecordingAlarm();
        var time = new ManualTimeProvider(Start);
        return (new ServiceTokenProvider(issuer, alarm, time), issuer, alarm, time);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_cache_hit_does_not_call_issuer_again()
    {
        var (p, issuer, _, _) = Make(Issued());
        Assert.IsType<TokenOutcome.Ok>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.IsType<TokenOutcome.Ok>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal(1, issuer.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_new_key_id_issues_a_new_token()
    {
        var (p, issuer, _, _) = Make(Issued(), Issued(Grant(keyId: "key-2")));
        await p.GetAsync(Session, Key("key-1"), TestContext.Current.CancellationToken);
        var second = await p.GetAsync(Session, Key("key-2"), TestContext.Current.CancellationToken);
        Assert.IsType<TokenOutcome.Ok>(second);
        Assert.Equal(2, issuer.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_expired_window_requests_a_fresh_token()
    {
        var (p, issuer, _, time) = Make(Issued(), Issued());
        await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30)); // past token expiry minus skew
        await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken);
        Assert.Equal(2, issuer.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_near_expiry_token_is_used_once_and_not_cached()
    {
        var near = Grant(expires: Start.AddSeconds(20));
        var (p, issuer, _, _) = Make(Issued(near), Issued(near));
        Assert.IsType<TokenOutcome.Ok>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.IsType<TokenOutcome.Ok>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal(2, issuer.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_issuer_unavailable_fails_without_alarm()
    {
        var (p, _, alarm, _) = Make(new TokenIssueResult.Unavailable());
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Empty(alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_issuer_refused_raises_scope_alarm()
    {
        var (p, _, alarm, _) = Make(new TokenIssueResult.Refused());
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal([AlarmCodes.TokenIssuerRefusedScope], alarm.Codes);
    }

    [Theory]
    [InlineData(401, AlarmCodes.TokenIssuer401)]
    [InlineData(403, AlarmCodes.TokenIssuer403)]
    [Trait("Category", "Unit")]
    public async Task IDN_05_issuer_caller_rejected_raises_status_alarm(int status, string code)
    {
        var (p, _, alarm, _) = Make(new TokenIssueResult.CallerRejected(status));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal([code], alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_06_token_outliving_session_key_is_rejected_and_not_cached()
    {
        var long_ = Grant(expires: Start.AddHours(2));
        var (p, issuer, alarm, _) = Make(Issued(long_), Issued(long_));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal(2, issuer.Calls);
        Assert.Equal([AlarmCodes.TokenOutlivesSessionKey, AlarmCodes.TokenOutlivesSessionKey], alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_06_cache_hit_is_revalidated_against_current_key_expiry()
    {
        var (p, _, alarm, _) = Make(Issued());
        await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken);
        var shorter = Key(expires: Start.AddMinutes(10));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, shorter, TestContext.Current.CancellationToken));
        Assert.Equal([AlarmCodes.TokenOutlivesSessionKey], alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_07_tenant_mismatch_is_discarded_with_alarm()
    {
        var (p, issuer, alarm, _) = Make(Issued(Grant(tenant: Guid.NewGuid())), Issued(Grant(tenant: Guid.NewGuid())));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal(2, issuer.Calls);
        Assert.Equal([AlarmCodes.TokenClaimsMismatch, AlarmCodes.TokenClaimsMismatch], alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_07_delegated_agent_mismatch_is_discarded_with_alarm()
    {
        var (p, _, alarm, _) = Make(Issued(Grant(agentId: "mallory")));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal([AlarmCodes.TokenClaimsMismatch], alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_07_key_id_mismatch_is_discarded_with_alarm()
    {
        var (p, _, alarm, _) = Make(Issued(Grant(keyId: "other")));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal([AlarmCodes.TokenClaimsMismatch], alarm.Codes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_07_agent_comparison_is_ordinal()
    {
        var (p, _, alarm, _) = Make(Issued(Grant(agentId: "LIAM")));
        Assert.IsType<TokenOutcome.Failed>(await p.GetAsync(Session, Key(), TestContext.Current.CancellationToken));
        Assert.Equal([AlarmCodes.TokenClaimsMismatch], alarm.Codes);
    }
}
