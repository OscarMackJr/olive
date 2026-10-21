using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Olive.Application;
using Olive.Domain;

namespace Olive.Integrations.Popeye;

// Fixture-driven stand-in for popeye key introspection (D-13). Non-production only (FND-05).
// Deliberate divergence from POPEYE_v0.4.2 section 2.1: user and application fixtures return active:true with their
// principal_type (the spec says non-agent keys return only active:false) so Olive's own principal check
// (403 principal_not_permitted, D section 7) is exercised. Flagged for Phase 3 popeye research.
public sealed partial class StubKeyIntrospector(IReadOnlyDictionary<string, string> keyToJson, TimeProvider time) : IKeyIntrospector, INonProductionOnly
{
    private const string Malformed = "malformed_introspection_response";

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    public static StubKeyIntrospector CreateDefault(TimeProvider time)
    {
        var asm = typeof(StubKeyIntrospector).Assembly;
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(asm, "introspection/index.json"))
            ?? throw new IntrospectionUnavailableException(Malformed);
        var map = index.ToDictionary(kv => kv.Key, kv => ReadResource(asm, "introspection/" + kv.Value), StringComparer.Ordinal);
        return new StubKeyIntrospector(map, time);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new IntrospectionUnavailableException(Malformed);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct)
    {
        if (!keyToJson.TryGetValue(key.ExposeSecret(), out var json))
        {
            return Task.FromResult(IntrospectionResult.Inactive);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("active", out var active))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            if (active.ValueKind == JsonValueKind.False)
            {
                return Task.FromResult(IntrospectionResult.Inactive);
            }

            if (active.ValueKind != JsonValueKind.True)
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var keyId = Str(root, "key_id");
            if (!UuidPattern().IsMatch(keyId))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var principalType = Str(root, "principal_type");
            if (!PrincipalTypes.All.Contains(principalType))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var agentId = OptStr(root, "agent_id");
            var onBehalfOf = OptStr(root, "on_behalf_of");
            if (principalType == PrincipalTypes.Agent && (agentId is null || onBehalfOf is null))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var tenantText = Str(root, "tenant_id");
            if (!UuidPattern().IsMatch(tenantText) || !Guid.TryParse(tenantText, out var tenantId))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var purpose = Str(root, "purpose");
            var ceiling = Str(root, "data_class_ceiling");
            if (!DateTimeOffset.TryParse(Str(root, "expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            if (expiresAt <= time.GetUtcNow())
            {
                return Task.FromResult(IntrospectionResult.Inactive);
            }

            return Task.FromResult(IntrospectionResult.From(
                new ActiveKey(keyId, principalType, agentId, onBehalfOf, purpose, tenantId, ceiling, expiresAt)));
        }
        catch (JsonException)
        {
            throw new IntrospectionUnavailableException(Malformed);
        }
    }

    private static string Str(JsonElement root, string name) =>
        OptStr(root, name) ?? throw new IntrospectionUnavailableException(Malformed);

    private static string? OptStr(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString()))
        {
            throw new IntrospectionUnavailableException(Malformed);
        }

        return v.GetString();
    }
}
