using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
using Olive.Domain;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

// D-16: fault cases live only in the test project. Production stubs have no toggles.
public enum ResolverFault { None, Unavailable, Hang, Throw, CallerRejected401, CallerRejected403, Malformed, NotFound }

public sealed class FaultingTraceResolver(ITraceResolver healthy, ResolverFault mode = ResolverFault.None) : ITraceResolver
{
    public ResolverFault Mode { get; set; } = mode;

    public async Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct)
    {
        switch (Mode)
        {
            case ResolverFault.Unavailable: return new TraceLookup.Unavailable();
            case ResolverFault.Throw: throw new HttpRequestException("resolver-fault-secret-detail");
            case ResolverFault.Hang:
                await Task.Delay(Timeout.Infinite, ct);
                return new TraceLookup.Unavailable();
            case ResolverFault.CallerRejected401: return new TraceLookup.CallerRejected(401);
            case ResolverFault.CallerRejected403: return new TraceLookup.CallerRejected(403);
            case ResolverFault.Malformed: return new TraceLookup.Malformed();
            case ResolverFault.NotFound: return new TraceLookup.NotFound();
            default: return await healthy.ResolveAsync(grant, traceId, ct);
        }
    }
}

public enum TokenFault { None, Unavailable, Hang, Throw, Refused, CallerRejected401, CallerRejected403, WrongTenant, WrongDelegatedAgent, WrongKeyId, ExpiresAfterKey }

public sealed class FaultingTokenIssuer(TokenFault mode = TokenFault.None) : ITokenIssuer
{
    private readonly StubKeyIntrospector _keys = StubKeyIntrospector.CreateDefault(TimeProvider.System);
    private readonly StubTokenIssuer _real = StubTokenIssuer.CreateDefault(TimeProvider.System);

    public TokenFault Mode { get; set; } = mode;

    public async Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct)
    {
        switch (Mode)
        {
            case TokenFault.Unavailable: return new TokenIssueResult.Unavailable();
            case TokenFault.Throw: throw new HttpRequestException("token-fault-secret-detail");
            case TokenFault.Hang:
                await Task.Delay(Timeout.Infinite, ct);
                return new TokenIssueResult.Unavailable();
            case TokenFault.Refused: return new TokenIssueResult.Refused();
            case TokenFault.CallerRejected401: return new TokenIssueResult.CallerRejected(401);
            case TokenFault.CallerRejected403: return new TokenIssueResult.CallerRejected(403);
        }

        var real = await _real.IssueAsync(key, ct);
        if (real is not TokenIssueResult.Issued issued || Mode == TokenFault.None)
        {
            return real;
        }

        var g = issued.Grant;
        var tenant = g.TenantId;
        var agent = g.DelegatedAgentId;
        var keyId = g.KeyId;
        var expires = g.ExpiresAt;
        switch (Mode)
        {
            case TokenFault.WrongTenant: tenant = Guid.NewGuid(); break;
            case TokenFault.WrongDelegatedAgent: agent = "not-liam"; break;
            case TokenFault.WrongKeyId: keyId = "other-key"; break;
            case TokenFault.ExpiresAfterKey: expires = (await _keys.IntrospectAsync(key, ct)).Key!.ExpiresAt.AddDays(1); break;
        }

        return new TokenIssueResult.Issued(new ServiceTokenGrant(g.Token, tenant, agent, keyId, expires));
    }
}

// Popeye rejecting Olive itself as a caller during key introspection (D-13).
public sealed class FaultingKeyIntrospector(int status) : IKeyIntrospector
{
    public Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct) =>
        throw new IntrospectionCallerRejectedException(status);
}

public sealed class FaultingUserIdentityResolver(IUserIdentityResolver healthy, ResolverFault mode = ResolverFault.None) : IUserIdentityResolver
{
    public ResolverFault Mode { get; set; } = mode;

    public async Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct)
    {
        switch (Mode)
        {
            case ResolverFault.Unavailable: return new UserIdentityLookup.Unavailable();
            case ResolverFault.Throw: throw new HttpRequestException("identity-fault-secret-detail");
            case ResolverFault.Hang:
                await Task.Delay(Timeout.Infinite, ct);
                return new UserIdentityLookup.Unavailable();
            case ResolverFault.Malformed: return new UserIdentityLookup.Malformed();
            default: return await healthy.ResolveAsync(tenantId, onBehalfOf, ct);
        }
    }
}

public sealed class FaultingDirectoryResolver(IDirectoryGroupResolver healthy, ResolverFault mode = ResolverFault.None) : IDirectoryGroupResolver
{
    public ResolverFault Mode { get; set; } = mode;

    public async Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct)
    {
        switch (Mode)
        {
            case ResolverFault.Unavailable: return new DirectoryLookup.Unavailable();
            case ResolverFault.Throw: throw new HttpRequestException("directory-fault-secret-detail");
            case ResolverFault.Hang:
                await Task.Delay(Timeout.Infinite, ct);
                return new DirectoryLookup.Unavailable();
            case ResolverFault.CallerRejected401: return new DirectoryLookup.CallerRejected(401);
            case ResolverFault.CallerRejected403: return new DirectoryLookup.CallerRejected(403);
            case ResolverFault.Malformed: return new DirectoryLookup.Malformed();
            default: return await healthy.ResolveAsync(tenantId, onBehalfOf, ct);
        }
    }
}

public enum ExecutionFault { None, ThrowOnBegin, ThrowOnStarted, ThrowOnFinish }

// Execution-path doubles (EXE-02, EXE-03, EXE-05). Tests only; the thrown text carries a sentinel so tests can prove it never leaks (D-09).
public sealed class FaultingExecutionStore(IExecutionStore inner, ExecutionFault mode = ExecutionFault.None, bool throwOnce = true) : IExecutionStore
{
    public const string Sentinel = "EXEC-FAULT-SENTINEL-4471";

    public ExecutionFault Mode { get; set; } = mode;

    public bool ThrowOnce { get; set; } = throwOnce;

    internal bool Take(ExecutionFault fault)
    {
        if (Mode != fault) { return false; }
        if (ThrowOnce) { Mode = ExecutionFault.None; }
        return true;
    }

    public async Task<IExecutionAttempt?> BeginAttemptAsync(Guid tenantId, Guid proposalId, CancellationToken ct)
    {
        if (Take(ExecutionFault.ThrowOnBegin)) { throw new InvalidOperationException(Sentinel); }
        var attempt = await inner.BeginAttemptAsync(tenantId, proposalId, ct);
        return attempt is null ? null : new FaultingAttempt(this, attempt);
    }

    public Task<IReadOnlyList<Guid>> ListApprovedAsync(Guid tenantId, CancellationToken ct) => inner.ListApprovedAsync(tenantId, ct);

    private sealed class FaultingAttempt(FaultingExecutionStore owner, IExecutionAttempt real) : IExecutionAttempt
    {
        public ValueTask DisposeAsync() => real.DisposeAsync();

        public Task<ExecutionSnapshot?> LoadAsync(CancellationToken ct) => real.LoadAsync(ct);

        public Task<LedgerRow> RecordStartedAsync(DateTimeOffset now, CancellationToken ct)
        {
            if (owner.Take(ExecutionFault.ThrowOnStarted)) { throw new InvalidOperationException(Sentinel); }
            return real.RecordStartedAsync(now, ct);
        }

        // Thrown before the real write: the started row and the target write exist, the finished row and state change do not.
        public Task<LedgerRow> RecordFinishedAsync(FinishedOutcome outcome, string toState, DateTimeOffset now, CancellationToken ct)
        {
            if (owner.Take(ExecutionFault.ThrowOnFinish)) { throw new InvalidOperationException(Sentinel); }
            return real.RecordFinishedAsync(outcome, toState, now, ct);
        }

        public Task<LedgerRow> RecordRefusalAsync(string reason, string toState, DateTimeOffset now, CancellationToken ct) =>
            real.RecordRefusalAsync(reason, toState, now, ct);
    }
}

// Signals when an attempt is inside the target write, then waits for the test to release it.
public sealed class GateExecutor(IActionExecutor inner) : IActionExecutor
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public Task Entered => _entered.Task;

    public int Calls => Volatile.Read(ref _calls);

    public void Release() => _release.TrySetResult();

    public async Task<ExecutionTargetResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        _entered.TrySetResult();
        await _release.Task.WaitAsync(ct);
        return await inner.ExecuteAsync(request, ct);
    }
}

public sealed class ThrowingExecutor : IActionExecutor
{
    public const string Sentinel = "EXECUTOR-THROW-SENTINEL-5582";

    public Task<ExecutionTargetResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct) =>
        throw new InvalidOperationException(Sentinel);
}

public sealed class CountingExecutor(IActionExecutor inner) : IActionExecutor
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<ExecutionTargetResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return inner.ExecuteAsync(request, ct);
    }
}

public sealed class SwitchableCatalogCheck(IActionCatalogCheck inner) : IActionCatalogCheck
{
    public bool? Executable { get; set; }

    public CatalogCheckResult Check(string actionId, string parametersJson) =>
        Executable == false ? new CatalogCheckResult(false, null) : inner.Check(actionId, parametersJson);
}

public static class ServiceDecoration
{
    // Replaces the registered T with decorate(real). The real instance is built by the original registration.
    public static void Decorate<T>(this IServiceCollection services, Func<T, T> decorate) where T : class
    {
        var original = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(original);
        services.AddSingleton<T>(sp =>
        {
            var real = original.ImplementationFactory is { } f ? (T)f(sp)
                : original.ImplementationInstance is { } i ? (T)i
                : (T)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return decorate(real);
        });
    }
}
