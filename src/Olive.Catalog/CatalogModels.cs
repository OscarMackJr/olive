using System.Text.Json;

namespace Olive.Catalog;

public sealed record CatalogEntry(
    string ActionId, JsonElement EntryJson, SchemaValidator ParametersSchema,
    IReadOnlyList<string>? RequiredPurpose = null, int MaxRateLimit = 0, int MaxRateWindowSeconds = 0,
    string? RequiredEntitlement = null, int MaxExpirySeconds = 0);

public sealed record CatalogRelease(int Version, IReadOnlyList<CatalogEntry> Entries);

// Immutable once built. There is deliberately no registration member (REQ-ACT-06): the catalog is source-controlled.
public sealed class CatalogSet
{
    private readonly IReadOnlyList<CatalogRelease> _releases;

    public CatalogSet(IReadOnlyList<CatalogRelease> releases)
    {
        if (releases.Count == 0)
        {
            throw new InvalidOperationException("Catalog is invalid.");
        }

        _releases = releases;
    }

    public IReadOnlyList<CatalogRelease> Releases => _releases;

    // D-13: the highest version is current.
    public CatalogRelease Current => _releases.MaxBy(r => r.Version)!;

    public bool TryGetRelease(int version, out CatalogRelease release)
    {
        release = _releases.FirstOrDefault(r => r.Version == version)!;
        return release is not null;
    }

    public bool TryFindAction(string actionId, out CatalogEntry entry)
    {
        entry = Current.Entries.FirstOrDefault(e => string.Equals(e.ActionId, actionId, StringComparison.Ordinal))!;
        return entry is not null;
    }
}
