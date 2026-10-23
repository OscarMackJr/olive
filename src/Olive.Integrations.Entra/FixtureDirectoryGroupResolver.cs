using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Olive.Application;

namespace Olive.Integrations.Entra;

// Fixture-backed stand-in for the directory group lookup. Non-production only (FND-05). Read-only, no credential.
// Keyed on (tenant id, on_behalf_of), the latter ordinal and verbatim (D-06). No caching layer (D-07).
public sealed class FixtureDirectoryGroupResolver : IDirectoryGroupResolver, INonProductionOnly
{
    private readonly ConcurrentDictionary<(Guid Tenant, string User), string[]> _groups = new();

    public static FixtureDirectoryGroupResolver CreateDefault()
    {
        var resolver = new FixtureDirectoryGroupResolver();
        var asm = typeof(FixtureDirectoryGroupResolver).Assembly;
        var files = JsonSerializer.Deserialize<string[]>(ReadResource(asm, "directory/index.json"))
            ?? throw new InvalidOperationException("Directory fixture index is malformed.");
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(ReadResource(asm, "directory/" + file));
            var root = doc.RootElement;
            var groups = root.GetProperty("groups").EnumerateArray().Select(g => g.GetString() ?? throw new InvalidOperationException("A directory fixture is malformed.")).ToArray();
            resolver.Register(Guid.Parse(root.GetProperty("tenant_id").GetString()!), root.GetProperty("on_behalf_of").GetString()!, groups);
        }

        return resolver;
    }

    public void Register(Guid tenantId, string onBehalfOf, IEnumerable<string> groups) => _groups[(tenantId, onBehalfOf)] = [.. groups];

    public Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct)
    {
        DirectoryLookup result = _groups.TryGetValue((tenantId, onBehalfOf), out var groups)
            ? new DirectoryLookup.Found(groups)
            : new DirectoryLookup.UnknownUser();
        return Task.FromResult(result);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException("A directory fixture resource is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
