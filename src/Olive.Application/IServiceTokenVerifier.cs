using Olive.Domain;

namespace Olive.Application;

// D-05: inbound service-token verification goes to popeye only, on its own port so session keys and service tokens never share a code path.
// Fail closed: an unavailable or malformed verifier response throws IntrospectionUnavailableException; nothing here catches it.
[OutboundPort(OutboundDestination.Popeye)]
public interface IServiceTokenVerifier { Task<ServiceTokenResult> VerifyAsync(InboundBearerToken token, CancellationToken ct); }

public sealed record ServiceCaller(string PrincipalType, string ClientId, Guid TenantId, string? DelegatedAgentId, DateTimeOffset ExpiresAt);

public sealed record ServiceTokenResult(bool Active, ServiceCaller? Caller)
{
    public static ServiceTokenResult Inactive { get; } = new(false, null);
    public static ServiceTokenResult From(ServiceCaller caller) => new(true, caller);
}
