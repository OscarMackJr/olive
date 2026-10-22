using Olive.Application;
using Olive.Domain;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

// D-16: fault cases live only in the test project. Production stubs have no toggles.
public enum ResolverFault { None, Unavailable, Hang, CallerRejected401, CallerRejected403, Malformed, NotFound }

public sealed class FaultingTraceResolver(ITraceResolver healthy, ResolverFault mode = ResolverFault.None) : ITraceResolver
{
    public ResolverFault Mode { get; set; } = mode;

    public async Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct)
    {
        switch (Mode)
        {
            case ResolverFault.Unavailable: return new TraceLookup.Unavailable();
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

public enum TokenFault { None, Unavailable, Hang, Refused, CallerRejected401, CallerRejected403, WrongTenant, WrongDelegatedAgent, WrongKeyId, ExpiresAfterKey }

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
