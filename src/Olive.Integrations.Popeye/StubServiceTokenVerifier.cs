using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Olive.Application;
using Olive.Domain;

namespace Olive.Integrations.Popeye;

// Fixture-driven stand-in for popeye inbound service-token verification (D-05). Non-production only (FND-05).
// Deliberate divergence, as for StubKeyIntrospector: the agent and user fixtures return active:true with their principal_type
// so Olive's own 403 logic runs. A real popeye adapter's behavior for those principals is unratified (assumption A1).
// Fail closed: a malformed fixture throws IntrospectionUnavailableException; nothing catches it and nothing defaults to allow.
public sealed partial class StubServiceTokenVerifier(IReadOnlyDictionary<string, string> tokenToJson, TimeProvider time) : IServiceTokenVerifier, INonProductionOnly
{
    private const string Malformed = "malformed_introspection_response";

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    public static StubServiceTokenVerifier CreateDefault(TimeProvider time)
    {
        var asm = typeof(StubServiceTokenVerifier).Assembly;
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(asm, "service-token/index.json"))
            ?? throw new IntrospectionUnavailableException(Malformed);
        var map = index.ToDictionary(kv => kv.Key, kv => ReadResource(asm, "service-token/" + kv.Value), StringComparer.Ordinal);
        return new StubServiceTokenVerifier(map, time);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new IntrospectionUnavailableException(Malformed);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public Task<ServiceTokenResult> VerifyAsync(InboundBearerToken token, CancellationToken ct)
    {
        if (!tokenToJson.TryGetValue(token.ExposeSecret(), out var json))
        {
            return Task.FromResult(ServiceTokenResult.Inactive);
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
                return Task.FromResult(ServiceTokenResult.Inactive);
            }

            if (active.ValueKind != JsonValueKind.True)
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var principalType = Str(root, "principal_type");
            if (!PrincipalTypes.All.Contains(principalType))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var clientId = Str(root, "client_id");
            var tenantText = Str(root, "tenant_id");
            if (!UuidPattern().IsMatch(tenantText) || !Guid.TryParse(tenantText, out var tenantId))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            var delegated = OptStr(root, "delegated_agent_id");
            if (!DateTimeOffset.TryParse(Str(root, "expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiresAt))
            {
                throw new IntrospectionUnavailableException(Malformed);
            }

            if (expiresAt <= time.GetUtcNow())
            {
                return Task.FromResult(ServiceTokenResult.Inactive);
            }

            return Task.FromResult(ServiceTokenResult.From(new ServiceCaller(principalType, clientId, tenantId, delegated, expiresAt)));
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
