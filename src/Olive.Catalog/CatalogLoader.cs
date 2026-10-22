using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Olive.Catalog;

// Loads the source-controlled catalog from YAML (D-14). Every fault throws InvalidOperationException with a static message
// that names no values, and the host turns that into a startup refusal. There is no runtime registration (REQ-ACT-06).
public static partial class CatalogLoader
{
    private const string Invalid = "Catalog is invalid.";
    private const string ManifestName = "catalog.yaml";

    private static readonly JsonSerializerOptions Strict = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static readonly Lazy<SchemaValidator> EntrySchema = new(() =>
    {
        using var s = typeof(CatalogLoader).Assembly.GetManifestResourceStream("contracts/catalog/v1/action-type.schema.json")
            ?? throw new InvalidOperationException(Invalid);
        using var r = new StreamReader(s);
        return SchemaValidator.Compile(r.ReadToEnd());
    });

    [GeneratedRegex(@"^[a-z0-9_.]+@[0-9]+$")]
    private static partial Regex ActionIdPattern();

    private sealed class EntryDoc
    {
        [JsonPropertyName("action_id")] public string? ActionId { get; set; }
        [JsonPropertyName("target_system")] public string? TargetSystem { get; set; }
        [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
        [JsonPropertyName("required_purpose")] public List<string>? RequiredPurpose { get; set; }
        [JsonPropertyName("required_approval")] public string? RequiredApproval { get; set; }
        [JsonPropertyName("reversible")] public bool? Reversible { get; set; }
        [JsonPropertyName("max_rate")] public RateDoc? MaxRate { get; set; }
    }

    private sealed class RateDoc
    {
        [JsonPropertyName("limit")] public int Limit { get; set; }
        [JsonPropertyName("window_seconds")] public int WindowSeconds { get; set; }
    }

    private sealed class ManifestDoc
    {
        [JsonPropertyName("catalog_version")] public int? CatalogVersion { get; set; }
        [JsonPropertyName("entries")] public List<string>? Entries { get; set; }
    }

    public static CatalogSet Parse(string manifestText, IReadOnlyDictionary<string, string> entryFilesByName, IReadOnlySet<string> purposes)
    {
        try
        {
            return ParseCore(manifestText, entryFilesByName, purposes);
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or JsonException or Json.Schema.JsonSchemaException
            or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new InvalidOperationException(Invalid);
        }
    }

    private static CatalogSet ParseCore(string manifestText, IReadOnlyDictionary<string, string> files, IReadOnlySet<string> purposes)
    {
        var manifestNode = YamlToJson.Parse(manifestText) ?? throw new InvalidOperationException(Invalid);
        var manifest = manifestNode.Deserialize<ManifestDoc>(Strict) ?? throw new InvalidOperationException(Invalid);
        if (manifest.CatalogVersion is not >= 1 || manifest.Entries is not { Count: > 0 } ids)
        {
            throw new InvalidOperationException(Invalid);
        }

        if (ids.Any(id => id is null || !ActionIdPattern().IsMatch(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new InvalidOperationException(Invalid);
        }

        var expected = ids.Select(id => id + ".yaml").ToHashSet(StringComparer.Ordinal);
        var onDisk = files.Keys.Where(n => !string.Equals(n, ManifestName, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(onDisk))
        {
            throw new InvalidOperationException(Invalid);
        }

        var entries = new List<CatalogEntry>();
        foreach (var id in ids)
        {
            entries.Add(ParseEntry(id, files[id + ".yaml"], purposes));
        }

        return new CatalogSet([new CatalogRelease(manifest.CatalogVersion.Value, entries)]);
    }

    private static CatalogEntry ParseEntry(string fileId, string text, IReadOnlySet<string> purposes)
    {
        var node = YamlToJson.Parse(text) ?? throw new InvalidOperationException(Invalid);
        var json = node.ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement.Clone();

        if (EntrySchema.Value.Validate(element) is not null)
        {
            throw new InvalidOperationException(Invalid);
        }

        var entry = JsonSerializer.Deserialize<EntryDoc>(json, Strict) ?? throw new InvalidOperationException(Invalid);
        if (entry.ActionId is not { } actionId || !ActionIdPattern().IsMatch(actionId) || !string.Equals(actionId, fileId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Invalid);
        }

        if (entry.RequiredPurpose is not { Count: > 0 } req || req.Any(p => !purposes.Contains(p)))
        {
            throw new InvalidOperationException(Invalid);
        }

        CatalogVetting.Check(entry.Parameters);
        return new CatalogEntry(actionId, element, SchemaValidator.Compile(entry.Parameters.GetRawText()));
    }

    public static CatalogSet LoadEmbedded(string vocabularyPath)
    {
        var asm = typeof(CatalogLoader).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("catalog/", StringComparison.Ordinal) && n.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            files[name["catalog/".Length..]] = r.ReadToEnd();
        }

        if (!files.TryGetValue(ManifestName, out var manifest))
        {
            throw new InvalidOperationException(Invalid);
        }

        return Parse(manifest, files, VocabularyPurposes.Load(vocabularyPath));
    }
}
