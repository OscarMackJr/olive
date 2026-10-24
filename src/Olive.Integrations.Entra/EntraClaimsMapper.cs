using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Olive.Application;

namespace Olive.Integrations.Entra;

public sealed record EntraMapResult(ReviewerIdentity Identity, Guid? TenantId, string? NoTenantReason, long AuthTime);

// D-07: app role values are named without the role: prefix; the allowlist is derived from the kind registry so a role exists only if a kind requires it.
public static class RoleAllowlist
{
    public static IReadOnlyDictionary<string, string> FromRegistry(IQueueKindRegistry registry)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in registry.Kinds)
        {
            if (registry.TryGet(kind, out var definition) && definition.RequiredRole.StartsWith(EntraClaimsMapper.RolePrefix, StringComparison.Ordinal))
            {
                map[definition.RequiredRole[EntraClaimsMapper.RolePrefix.Length..]] = definition.RequiredRole;
            }
        }

        return map;
    }
}

// D-01 / D-04 / D-07 / D-08: pure mapping from raw id_token claims to the Olive reviewer identity. No claim value ever appears in a message.
public static class EntraClaimsMapper
{
    public const string RolePrefix = "role:";
    public const string GroupsOverage = "groups_overage";
    public const string NoReviewerTenant = "no_reviewer_tenant";

    public static EntraMapResult Map(IEnumerable<Claim> claims, IReviewerTenantMap tenants, IReadOnlyDictionary<string, string> roleAllowlist, string? expectedTid)
    {
        var all = claims.ToList();
        const string missing = "The token is missing a required identity claim.";
        if (!Guid.TryParse(First(all, "oid"), out var oid) || !Guid.TryParse(First(all, "tid"), out var tid))
        {
            throw new InvalidOperationException(missing);
        }

        if (expectedTid is not null && (!Guid.TryParse(expectedTid, out var expected) || expected != tid))
        {
            throw new InvalidOperationException(missing);
        }

        var groupValues = Values(all, "groups").Where(g => !g.StartsWith(RolePrefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var groups = new List<string>(groupValues);
        foreach (var role in Values(all, "roles"))
        {
            if (roleAllowlist.TryGetValue(role, out var mapped) && !groups.Contains(mapped, StringComparer.Ordinal))
            {
                groups.Add(mapped);
            }
        }

        var overage = IsOverage(all);
        Guid? tenant = null;
        if (!overage)
        {
            var found = tenants.TenantsFor(groupValues).Distinct().ToList();
            tenant = found.Count == 1 ? found[0] : null;
        }

        var reason = overage ? GroupsOverage : tenant is null ? NoReviewerTenant : null;
        var oidText = oid.ToString("D");
        var identity = new ReviewerIdentity(oidText, oidText, tid.ToString("D"), First(all, "preferred_username") ?? "", groups);
        return new EntraMapResult(identity, tenant, reason, AuthTime(all));
    }

    private static string? First(List<Claim> claims, string type) => claims.FirstOrDefault(c => c.Type == type)?.Value;

    // Handles one claim per value and a single claim whose value is a JSON array.
    private static IEnumerable<string> Values(List<Claim> claims, string type)
    {
        foreach (var claim in claims.Where(c => c.Type == type))
        {
            var value = claim.Value;
            if (value.StartsWith('[') && TryParseArray(value) is { } items)
            {
                foreach (var item in items) { yield return item; }
            }
            else if (value.Length > 0)
            {
                yield return value;
            }
        }
    }

    private static List<string>? TryParseArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { return null; }
            return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // D-04: a hasgroups claim, or a _claim_names object naming groups. Malformed input is not overage and never throws.
    private static bool IsOverage(List<Claim> claims)
    {
        if (claims.Any(c => c.Type == "hasgroups")) { return true; }
        foreach (var claim in claims.Where(c => c.Type == "_claim_names"))
        {
            try
            {
                using var doc = JsonDocument.Parse(claim.Value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("groups", out _)) { return true; }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static long AuthTime(List<Claim> claims) =>
        long.TryParse(First(claims, "auth_time"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
}
