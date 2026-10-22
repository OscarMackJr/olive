using System.Diagnostics;
using Olive.Domain;

namespace Olive.Application;

// FND-06, D17.1: a sealed class, never a record, so no generated ToString can print the token.
[DebuggerDisplay("[REDACTED]")]
public sealed class ServiceTokenGrant(ServiceToken token, Guid tenantId, string delegatedAgentId, string keyId, DateTimeOffset expiresAt) : IFormattable
{
    public ServiceToken Token { get; } = token;
    public Guid TenantId { get; } = tenantId;
    public string DelegatedAgentId { get; } = delegatedAgentId;
    public string KeyId { get; } = keyId;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public override string ToString() => "[REDACTED ServiceTokenGrant]";

    public string ToString(string? format, IFormatProvider? formatProvider) => "[REDACTED ServiceTokenGrant]";
}
