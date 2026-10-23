using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Olive.Application;

namespace Olive.Integrations.Entra;

// Fixture-backed stand-in for the user identity lookup (D-06). Non-production only (FND-05). Read-only, no credential.
// Keyed on (tenant id, on_behalf_of), the latter ordinal and verbatim. More than one record for a key is Ambiguous.
public sealed class FixtureUserIdentityResolver : IUserIdentityResolver, INonProductionOnly
{
    private readonly ConcurrentDictionary<(Guid Tenant, string User), List<(string Oid, string Tid)>> _records = new();

    public static FixtureUserIdentityResolver CreateDefault()
    {
        var resolver = new FixtureUserIdentityResolver();
        var asm = typeof(FixtureUserIdentityResolver).Assembly;
        var files = JsonSerializer.Deserialize<string[]>(ReadResource(asm, "identity/index.json"))
            ?? throw new InvalidOperationException("Identity fixture index is malformed.");
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(ReadResource(asm, "identity/" + file));
            var root = doc.RootElement;
            resolver.Register(
                Guid.Parse(root.GetProperty("tenant_id").GetString()!),
                root.GetProperty("on_behalf_of").GetString()!,
                root.GetProperty("oid").GetString()!,
                root.GetProperty("tid").GetString()!);
        }

        return resolver;
    }

    public void Register(Guid tenantId, string onBehalfOf, string oid, string tid) =>
        _records.AddOrUpdate((tenantId, onBehalfOf), _ => [(oid, tid)], (_, list) => { lock (list) { list.Add((oid, tid)); } return list; });

    public Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct)
    {
        UserIdentityLookup result;
        if (string.IsNullOrWhiteSpace(onBehalfOf))
        {
            result = new UserIdentityLookup.Malformed();
        }
        else if (!_records.TryGetValue((tenantId, onBehalfOf), out var list))
        {
            result = new UserIdentityLookup.NotFound();
        }
        else
        {
            lock (list)
            {
                result = list.Count switch
                {
                    0 => new UserIdentityLookup.NotFound(),
                    1 => new UserIdentityLookup.Resolved(list[0].Oid, list[0].Tid),
                    _ => new UserIdentityLookup.Ambiguous(),
                };
            }
        }

        return Task.FromResult(result);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException("An identity fixture resource is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
