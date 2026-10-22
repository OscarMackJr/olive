using System.Collections.Concurrent;
using Olive.Domain;

namespace Olive.Application;

public abstract record TokenOutcome
{
    public sealed record Ok(ServiceTokenGrant Grant) : TokenOutcome;
    // Cause is a static snake_case code for Warning logs only; never shown to the caller (D-06).
    // ExceptionType is the exception type name only (never its message) when the issuer threw (WR-02).
    public sealed record Failed(string Cause, string? ExceptionType = null) : TokenOutcome;
}

// The only type that ever hands a SessionKey to an outbound port (D17.1). Time comes only from the injected TimeProvider.
public sealed class ServiceTokenProvider(ITokenIssuer issuer, IOperationalAlarm alarm, TimeProvider time, TimeSpan? skew = null)
{
    private const int Capacity = 10_000;
    private const string Component = "popeye";
    private readonly TimeSpan _skew = skew ?? TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<(Guid TenantId, string AgentId, string KeyId), Entry> _cache = new();

    private sealed record Entry(ServiceTokenGrant Grant, DateTimeOffset UsableUntil);

    public async Task<TokenOutcome> GetAsync(SessionKey sessionKey, ActiveKey key, CancellationToken ct)
    {
        var cacheKey = (key.TenantId, key.AgentId ?? string.Empty, key.KeyId);
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out var hit))
        {
            if (!Agrees(hit.Grant, key))
            {
                _cache.TryRemove(cacheKey, out _);
                alarm.Raise(AlarmCodes.TokenClaimsMismatch, Component);
                return new TokenOutcome.Failed("token_claims_mismatch");
            }

            if (hit.Grant.ExpiresAt > key.ExpiresAt)
            {
                _cache.TryRemove(cacheKey, out _);
                alarm.Raise(AlarmCodes.TokenOutlivesSessionKey, Component);
                return new TokenOutcome.Failed("token_outlives_session_key");
            }

            if (now < hit.UsableUntil)
            {
                return new TokenOutcome.Ok(hit.Grant);
            }

            _cache.TryRemove(cacheKey, out _);
        }

        TokenIssueResult result;
        try
        {
            result = await issuer.IssueAsync(sessionKey, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Fail closed and retryable (D-06); the message may carry secrets, so only the type name is kept.
            return new TokenOutcome.Failed("token_issuer_exception", e.GetType().Name);
        }

        switch (result)
        {
            case TokenIssueResult.Unavailable:
                return new TokenOutcome.Failed("token_issuer_unavailable");
            case TokenIssueResult.Refused:
                alarm.Raise(AlarmCodes.TokenIssuerRefusedScope, Component);
                return new TokenOutcome.Failed("token_issuer_refused_scope");
            case TokenIssueResult.CallerRejected rejected:
                alarm.Raise(rejected.Status == 403 ? AlarmCodes.TokenIssuer403 : AlarmCodes.TokenIssuer401, Component);
                return new TokenOutcome.Failed(rejected.Status == 403 ? "token_issuer_403" : "token_issuer_401");
            case not TokenIssueResult.Issued:
                return new TokenOutcome.Failed("token_issuer_unavailable");
        }

        var grant = ((TokenIssueResult.Issued)result).Grant;
        if (!Agrees(grant, key))
        {
            alarm.Raise(AlarmCodes.TokenClaimsMismatch, Component);
            return new TokenOutcome.Failed("token_claims_mismatch");
        }

        if (grant.ExpiresAt > key.ExpiresAt)
        {
            alarm.Raise(AlarmCodes.TokenOutlivesSessionKey, Component);
            return new TokenOutcome.Failed("token_outlives_session_key");
        }

        var usableUntil = (grant.ExpiresAt < key.ExpiresAt ? grant.ExpiresAt : key.ExpiresAt) - _skew;
        if (usableUntil > now)
        {
            if (_cache.Count >= Capacity)
            {
                _cache.Clear();
            }

            _cache[cacheKey] = new Entry(grant, usableUntil);
        }

        return new TokenOutcome.Ok(grant);
    }

    private static bool Agrees(ServiceTokenGrant grant, ActiveKey key) =>
        grant.TenantId == key.TenantId
        && string.Equals(grant.DelegatedAgentId, key.AgentId, StringComparison.Ordinal)
        && string.Equals(grant.KeyId, key.KeyId, StringComparison.Ordinal);
}
