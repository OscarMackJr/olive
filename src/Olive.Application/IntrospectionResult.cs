namespace Olive.Application;

public sealed record ActiveKey(string KeyId, string PrincipalType, string? AgentId, string? OnBehalfOf, string Purpose, Guid TenantId, string DataClassCeiling, DateTimeOffset ExpiresAt);

public sealed record IntrospectionResult(bool Active, ActiveKey? Key)
{
    public static IntrospectionResult Inactive { get; } = new(false, null);
    public static IntrospectionResult From(ActiveKey key) => new(true, key);
}
