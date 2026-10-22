using System.Security.Cryptography;
using Olive.Application;
using Olive.Domain;

namespace Olive.Integrations.Popeye;

// Fixture stand-in for popeye service-token issuance (D-10, Olive-proposed shape). Non-production only (IDN-05, FND-05).
// Never logs, no fault toggles (D-16). Uses its own introspector so recording decorators on the DI one stay clean.
public sealed class StubTokenIssuer(IKeyIntrospector introspector, TimeProvider time) : ITokenIssuer, INonProductionOnly
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly Guid Alpha = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");
    private static readonly Guid Beta = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");

    public static StubTokenIssuer CreateDefault(TimeProvider time) => new(StubKeyIntrospector.CreateDefault(time), time);

    public async Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct)
    {
        var result = await introspector.IntrospectAsync(key, ct);
        if (!result.Active || result.Key is not { } k || k.AgentId != "liam" || (k.TenantId != Alpha && k.TenantId != Beta))
        {
            return new TokenIssueResult.Refused();
        }

        var expires = time.GetUtcNow().Add(Lifetime);
        if (k.ExpiresAt < expires)
        {
            expires = k.ExpiresAt;
        }

        var token = new ServiceToken(Base64Url(RandomNumberGenerator.GetBytes(32)));
        return new TokenIssueResult.Issued(new ServiceTokenGrant(token, k.TenantId, k.AgentId, k.KeyId, expires));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
