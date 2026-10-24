using Olive.Application;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Olive.Catalog;

public sealed class ReviewerTenantMap : IReviewerTenantMap
{
    private const string Invalid = "Reviewer tenant map is invalid.";

    private readonly Dictionary<string, Guid> _groups;

    private ReviewerTenantMap(Dictionary<string, Guid> groups) => _groups = groups;

    private sealed class MapDocument { public List<TenantEntry>? Tenants { get; set; } }

    private sealed class TenantEntry { public string? Group { get; set; } public string? TenantId { get; set; } }

    public static ReviewerTenantMap Parse(string yaml)
    {
        MapDocument? doc;
        try
        {
            // Strict mapping: IgnoreUnmatchedProperties is deliberately not set.
            doc = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
                .Deserialize<MapDocument?>(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            throw new InvalidOperationException(Invalid);
        }

        if (doc?.Tenants is not { Count: > 0 } entries)
        {
            throw new InvalidOperationException(Invalid);
        }

        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e?.Group is not { } group || string.IsNullOrWhiteSpace(group) || map.ContainsKey(group)
                || e.TenantId is not { } t || !Guid.TryParse(t, out var tenant) || tenant == Guid.Empty)
            {
                throw new InvalidOperationException(Invalid);
            }

            map[group] = tenant;
        }

        return new ReviewerTenantMap(map);
    }

    public static ReviewerTenantMap LoadEmbedded()
    {
        using var s = typeof(ReviewerTenantMap).Assembly.GetManifestResourceStream("config/reviewer-tenants.yaml")
            ?? throw new InvalidOperationException(Invalid);
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public IReadOnlyCollection<Guid> AllTenants => _groups.Values.Distinct().ToArray();

    public IReadOnlyCollection<Guid> TenantsFor(IEnumerable<string> groups) =>
        groups.Where(g => g is not null && _groups.ContainsKey(g)).Select(g => _groups[g]).Distinct().ToArray();
}
