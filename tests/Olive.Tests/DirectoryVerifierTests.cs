using Olive.Application;

namespace Olive.Tests;

public sealed class DirectoryVerifierTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private sealed class Fake(Func<CancellationToken, Task<DirectoryLookup>> f) : IDirectoryGroupResolver
    {
        public Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) => f(ct);
    }

    private static DirectoryVerifier Make(Func<CancellationToken, Task<DirectoryLookup>> f, RecordingAlarm alarm, TimeSpan? timeout = null) =>
        new(new Fake(f), alarm, new TraceVerifierOptions(timeout ?? TimeSpan.FromSeconds(5)));

    private static Task<DirectoryLookup> Ok(DirectoryLookup l) => Task.FromResult(l);

    [Fact]
    public async Task POL_02_found_returns_groups()
    {
        var v = await Make(_ => Ok(new DirectoryLookup.Found(["g1"])), new RecordingAlarm()).VerifyAsync(Tenant, "u", default);
        Assert.Equal(["g1"], Assert.IsType<DirectoryVerdict.Groups>(v).Names);
    }

    [Fact]
    public async Task POL_02_unknown_user_is_not_found_verdict()
    {
        var v = await Make(_ => Ok(new DirectoryLookup.UnknownUser()), new RecordingAlarm()).VerifyAsync(Tenant, "u", default);
        Assert.IsType<DirectoryVerdict.UnknownUser>(v);
    }

    [Fact]
    public async Task POL_02_unavailable_is_unavailable()
    {
        var v = await Make(_ => Ok(new DirectoryLookup.Unavailable()), new RecordingAlarm()).VerifyAsync(Tenant, "u", default);
        Assert.IsType<DirectoryVerdict.Unavailable>(v);
    }

    [Fact]
    public async Task POL_02_timeout_is_unavailable()
    {
        var v = await Make(async ct => { await Task.Delay(Timeout.Infinite, ct); return new DirectoryLookup.UnknownUser(); }, new RecordingAlarm(), TimeSpan.FromMilliseconds(50)).VerifyAsync(Tenant, "u", default);
        Assert.Equal("directory_resolver_timeout", Assert.IsType<DirectoryVerdict.Unavailable>(v).Cause);
    }

    [Fact]
    public async Task POL_02_throw_is_unavailable_with_type_name_only()
    {
        var v = await Make(_ => throw new InvalidOperationException("secret-upn@example.test"), new RecordingAlarm()).VerifyAsync(Tenant, "u", default);
        var u = Assert.IsType<DirectoryVerdict.Unavailable>(v);
        Assert.Equal("InvalidOperationException", u.ExceptionType);
        Assert.DoesNotContain("secret", u.Cause + u.ExceptionType);
    }

    [Theory]
    [InlineData(401, "directory_resolver_401")]
    [InlineData(403, "directory_resolver_403")]
    public async Task POL_02_caller_rejected_alarms_and_is_unavailable(int status, string code)
    {
        var alarm = new RecordingAlarm();
        var v = await Make(_ => Ok(new DirectoryLookup.CallerRejected(status)), alarm).VerifyAsync(Tenant, "u", default);
        Assert.IsType<DirectoryVerdict.Unavailable>(v);
        Assert.Equal([code], alarm.Codes);
    }

    [Fact]
    public async Task POL_02_malformed_alarms_and_is_unavailable()
    {
        var alarm = new RecordingAlarm();
        var v = await Make(_ => Ok(new DirectoryLookup.Malformed()), alarm).VerifyAsync(Tenant, "u", default);
        Assert.IsType<DirectoryVerdict.Unavailable>(v);
        Assert.Equal(["directory_response_malformed"], alarm.Codes);
    }
}
