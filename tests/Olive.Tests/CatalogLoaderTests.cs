using System.Text.Json;
using Olive.Catalog;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class CatalogLoaderTests
{
    private static readonly IReadOnlySet<string> Purposes = new HashSet<string> { "servicing", "origination", "collections", "analytics", "marketing", "compliance-review", "internal-support" };

    private const string Manifest = "catalog_version: 1\nentries:\n  - crm.case_note@1\n";

    private const string Entry = """
        action_id: crm.case_note@1
        target_system: nexus_crm
        parameters:
          type: object
          additionalProperties: false
          properties:
            case_ref: { type: string }
          required: [case_ref]
        required_purpose: [servicing]
        required_approval: role:case_supervisor
        reversible: true
        max_rate:
          limit: 10
          window_seconds: 3600
        """;

    private static IReadOnlyDictionary<string, string> Files(string entry = Entry, string name = "crm.case_note@1.yaml") =>
        new Dictionary<string, string> { [name] = entry };

    private static CatalogSet Load(string manifest, IReadOnlyDictionary<string, string> files) => CatalogLoader.Parse(manifest, files, Purposes);

    private static string RealVocabulary => Path.Combine(TestPaths.Root, "contracts", "vendored", "vocabulary.yaml");

    private static string WithParameters(string parametersYaml) =>
        Entry.Replace(Entry[Entry.IndexOf("parameters:", StringComparison.Ordinal)..Entry.IndexOf("required_purpose:", StringComparison.Ordinal)], parametersYaml + "\n");

    [Fact]
    public void API_10_real_catalog_loads_with_typed_scalars()
    {
        var set = CatalogLoader.LoadEmbedded(RealVocabulary);
        Assert.Equal(1, set.Current.Version);
        var entry = Assert.Single(set.Current.Entries);
        Assert.Equal("crm.case_note@1", entry.ActionId);
        Assert.True(set.TryFindAction("crm.case_note@1", out _));
        var json = entry.EntryJson;
        Assert.Equal(JsonValueKind.True, json.GetProperty("reversible").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("max_rate").GetProperty("limit").ValueKind);
        Assert.Equal("nexus_crm", json.GetProperty("target_system").GetString());
        Assert.Equal("object", json.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void API_10_quoted_scalars_stay_strings()
    {
        var set = Load(Manifest, Files(Entry.Replace("target_system: nexus_crm", "target_system: \"true\"")));
        Assert.Equal(JsonValueKind.String, set.Current.Entries[0].EntryJson.GetProperty("target_system").ValueKind);
    }

    [Fact]
    public void API_10_catalog_has_no_runtime_registration_member()
    {
        var types = new[] { typeof(CatalogSet), typeof(CatalogLoader), typeof(CatalogRelease), typeof(CatalogEntry) };
        foreach (var t in types)
        {
            Assert.DoesNotContain(t.GetMembers().Select(m => m.Name), n => n.Contains("AddAtRuntime") || n.StartsWith("Register") || n.StartsWith("Add", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void API_10_no_ignore_unmatched_properties_in_catalog_sources()
    {
        var dir = Path.Combine(TestPaths.Root, "src", "Olive.Catalog");
        var hits = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Contains("IgnoreUnmatchedProperties"));
        Assert.Empty(hits);
    }

    [Fact]
    public void API_10_duplicate_key_in_entry_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(Entry + "\nreversible: false")));
    }

    [Fact]
    public void API_10_duplicate_key_in_manifest_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Load("catalog_version: 1\ncatalog_version: 2\nentries: [crm.case_note@1]", Files()));
    }

    [Fact]
    public void API_10_unknown_top_level_key_in_entry_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(Entry + "\nextra: 1")));
    }

    [Fact]
    public void API_10_unknown_manifest_key_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest + "extra: 1\n", Files()));
    }

    [Theory]
    [InlineData("Crm.Case@1")]
    [InlineData("crm.case_note")]
    [InlineData("crm.other@1")]
    public void API_10_bad_or_mismatched_action_id_fails(string id)
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(Entry.Replace("action_id: crm.case_note@1", "action_id: " + id))));
    }

    [Fact]
    public void API_10_unknown_purpose_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(Entry.Replace("[servicing]", "[bogus]"))));
    }

    [Fact]
    public void API_10_entry_failing_action_type_schema_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(Entry.Replace("reversible: true", "reversible: \"true\""))));
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(Entry.Replace("limit: 10", "limit: 0"))));
    }

    [Fact]
    public void API_10_manifest_entry_without_file_fails()
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, new Dictionary<string, string>()));
    }

    [Fact]
    public void API_10_unlisted_catalog_file_fails()
    {
        var files = new Dictionary<string, string> { ["crm.case_note@1.yaml"] = Entry, ["crm.other@1.yaml"] = Entry };
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, files));
    }

    [Fact]
    public void API_10_duplicate_and_empty_manifest_entries_fail()
    {
        Assert.Throws<InvalidOperationException>(() => Load("catalog_version: 1\nentries: [crm.case_note@1, crm.case_note@1]", Files()));
        Assert.Throws<InvalidOperationException>(() => Load("catalog_version: 1\nentries: []", Files()));
        Assert.Throws<InvalidOperationException>(() => Load("catalog_version: 0\nentries: [crm.case_note@1]", Files()));
    }

    [Fact]
    public void API_10_failure_message_names_no_values()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(Entry.Replace("[servicing]", "[secretvalue]"))));
        Assert.DoesNotContain("secretvalue", e.Message);
    }

    [Theory]
    [InlineData("parameters:\n  type: object\n  properties:\n    a: { type: string }")]
    [InlineData("parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a:\n      type: object\n      properties:\n        b: { type: string }")]
    [InlineData("parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a:\n      type: array\n      items:\n        type: object")]
    [InlineData("parameters:\n  type: object\n  additionalProperties: false\n  $defs:\n    d:\n      type: object\n  properties:\n    a: { type: string }")]
    [InlineData("parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a: { $ref: 'https://example.invalid/x.json' }")]
    [InlineData("parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a: { $ref: 'other.json#/x' }")]
    [InlineData("parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a: { type: string, default: x }")]
    [InlineData("parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a: { type: string, format: email }")]
    public void API_10_d02_vetting_rejects_open_or_unsafe_schemas(string parameters)
    {
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(WithParameters(parameters))));
    }

    // CR-01: closure is an allowlist. Every node must name what it admits, `true` is refused and arrays must bound their items.
    [Theory]
    [InlineData("a: true")]
    [InlineData("a: {}")]
    [InlineData("a: { type: array }")]
    [InlineData("a: { type: array, prefixItems: [ { type: string } ] }")]
    [InlineData("a: { type: array, items: true }")]
    [InlineData("a: { type: [string, array] }")]
    [InlineData("a: { not: { type: string } }")]
    [InlineData("a: { required: [x] }")]
    [InlineData("a: { anyOf: [ { type: string }, {} ] }")]
    [InlineData("a: { type: string, not: true }")]
    public void API_10_d02_vetting_refuses_untyped_true_and_itemless_array_subschemas(string property)
    {
        var p = "parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    " + property;
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(WithParameters(p))));
    }

    [Fact]
    public void API_10_d02_vetting_accepts_typed_arrays_enums_and_typed_combinators()
    {
        var p = "parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a: { type: array, items: { type: string } }\n    b: { enum: [x, y] }\n    c: { const: z }\n    d: { anyOf: [ { type: string }, { type: integer } ] }\n    e: { type: array, items: false }";
        var schema = Assert.Single(Load(Manifest, Files(WithParameters(p))).Current.Entries).ParametersSchema;
        using var nested = JsonDocument.Parse("""{"a":[{"x":1}]}""");
        Assert.NotNull(schema.Validate(nested.RootElement));
    }

    // WR-01: schema-library compile faults (unknown keywords) map to the static catalog refusal, never the library's message.
    [Fact]
    public void API_10_schema_compile_fault_is_static_invalid_operation()
    {
        var p = "parameters:\n  type: object\n  additionalProperties: false\n  properties:\n    a: { type: string, x-hidden: secretvalue }";
        var e = Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(WithParameters(p))));
        Assert.Equal("Catalog is invalid.", e.Message);
        Assert.DoesNotContain("secretvalue", e.Message);
        Assert.DoesNotContain("x-hidden", e.Message);
    }

    // CR-02: a reference must resolve, at load time, to a vetted schema node. Otherwise every submission would fail at request time.
    [Theory]
    [InlineData("a: { $ref: '#/$defs/missing' }")]
    [InlineData("a: { $ref: '#/properties' }")]
    [InlineData("a: { $ref: '#/required' }")]
    [InlineData("a: { $ref: '#/properties/b' }")]
    [InlineData("a: { $ref: '#anchor' }")]
    [InlineData("a: { type: string, $anchor: anchor }")]
    [InlineData("a: { type: string, $id: 'https://example.invalid/a' }")]
    [InlineData("a: { type: string, $dynamicRef: '#' }")]
    public void API_10_d02_vetting_refuses_dangling_or_non_schema_references(string property)
    {
        var p = "parameters:\n  type: object\n  additionalProperties: false\n  required: [a]\n  properties:\n    " + property;
        Assert.Throws<InvalidOperationException>(() => Load(Manifest, Files(WithParameters(p))));
    }

    [Fact]
    public void API_10_d02_vetting_accepts_references_to_escaped_and_root_pointers()
    {
        var p = "parameters:\n  type: object\n  additionalProperties: false\n  $defs:\n    'x/y~z': { type: string }\n  properties:\n    a: { $ref: '#/$defs/x~1y~0z' }\n    b: { $ref: '#' }\n    c: { $ref: '#/properties/a' }";
        var schema = Assert.Single(Load(Manifest, Files(WithParameters(p))).Current.Entries).ParametersSchema;
        using var ok = JsonDocument.Parse("""{"a":"s","b":{"a":"t"},"c":"u"}""");
        Assert.Null(schema.Validate(ok.RootElement));
    }

    [Fact]
    public void API_10_d02_vetting_accepts_closed_nested_schema_with_local_ref_and_allowed_format()
    {
        var p = "parameters:\n  type: object\n  additionalProperties: false\n  $defs:\n    n:\n      type: object\n      additionalProperties: false\n      properties:\n        id: { type: string, format: uuid }\n  properties:\n    a: { $ref: '#/$defs/n' }";
        var set = Load(Manifest, Files(WithParameters(p)));
        Assert.Single(set.Current.Entries);
    }

    [Fact]
    public void API_10_compiled_parameters_schema_validates_instances()
    {
        var entry = CatalogLoader.LoadEmbedded(RealVocabulary).Current.Entries[0];
        using var ok = JsonDocument.Parse("""{"case_ref":"a","note_ref":"b"}""");
        using var extra = JsonDocument.Parse("""{"case_ref":"a","note_ref":"b","x":1}""");
        Assert.Null(entry.ParametersSchema.Validate(ok.RootElement));
        Assert.NotNull(entry.ParametersSchema.Validate(extra.RootElement));
    }
}
