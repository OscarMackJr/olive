using System.Text.Json;
using Olive.Catalog;
using Olive.Domain;

namespace Olive.Tests;

public sealed class JcsVectorTests
{
    private static string Dir => Path.Combine(TestPaths.Root, "contracts", "jcs", "v1");

    private static JsonElement Manifest => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "vectors.json"))).RootElement.Clone();

    private static SchemaValidator ManifestSchema => SchemaValidator.Compile(File.ReadAllText(Path.Combine(Dir, "vectors.schema.json")));

    public static TheoryData<string> CanonicalCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Manifest.GetProperty("cases").EnumerateArray().Where(c => !c.TryGetProperty("expect", out _)))
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    private static JsonElement Case(string name) => Manifest.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static IEnumerable<string> NumberTokens(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                yield return e.GetRawText();
                break;
            case JsonValueKind.Array:
                foreach (var t in e.EnumerateArray().SelectMany(NumberTokens))
                {
                    yield return t;
                }

                break;
            case JsonValueKind.Object:
                foreach (var t in e.EnumerateObject().SelectMany(p => NumberTokens(p.Value)))
                {
                    yield return t;
                }

                break;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalCases))]
    [Trait("Category", "Unit")]
    public void API_09_vector_recomputes_canonical_bytes_and_sha256(string name)
    {
        var c = Case(name);
        var input = File.ReadAllBytes(Path.Combine(Dir, c.GetProperty("input").GetString()!));
        var expected = File.ReadAllBytes(Path.Combine(Dir, c.GetProperty("canonical").GetString()!));
        using var doc = JsonDocument.Parse(input);
        var actual = c.GetProperty("kind").GetString() == "proposal_hash"
            ? Jcs.CanonicalizeProposalFields(doc.RootElement)
            : Jcs.Canonicalize(doc.RootElement);
        Assert.Equal(expected, actual);
        Assert.Equal(c.GetProperty("sha256").GetString(), Jcs.Sha256Hex(actual));
        if (c.GetProperty("kind").GetString() == "proposal_hash")
        {
            Assert.Equal(c.GetProperty("sha256").GetString(), Jcs.ProposalHash(doc.RootElement));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_lossy_number_gate_verdicts_match_json_numbers_rule()
    {
        foreach (var c in Manifest.GetProperty("cases").EnumerateArray())
        {
            var gate = c.GetProperty("gate").GetString();
            if (gate is "reject_duplicate_key" or "reject_lone_surrogate")
            {
                continue; // asserted by the gate tests in plan 04
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Dir, c.GetProperty("input").GetString()!)));
            var anyLossy = NumberTokens(doc.RootElement).Any(t => !JsonNumbers.IsLossless(t));
            Assert.Equal(gate == "reject_lossy_number", anyLossy);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_number_spellings_share_one_hash()
    {
        var hashes = new[] { "olive-number-1-0", "olive-number-1", "olive-number-1e0" }
            .Select(n => Case(n).GetProperty("sha256").GetString()).Distinct().ToList();
        Assert.Single(hashes);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_manifest_conforms_to_schema()
    {
        var m = Manifest;
        Assert.Null(ManifestSchema.Validate(m));
        Assert.Equal(1, m.GetProperty("version").GetInt32());
        Assert.NotEmpty(m.GetProperty("source").EnumerateArray());
        foreach (var c in m.GetProperty("cases").EnumerateArray())
        {
            var reject = c.TryGetProperty("expect", out _);
            Assert.Equal(!reject, c.TryGetProperty("canonical", out _));
            Assert.Equal(!reject, c.TryGetProperty("sha256", out _));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_manifest_covers_every_reject_category()
    {
        var rejects = Manifest.GetProperty("cases").EnumerateArray().Where(c => c.TryGetProperty("expect", out _)).ToList();
        Assert.True(rejects.Count >= 6);
        foreach (var gate in new[] { "reject_lossy_number", "reject_duplicate_key", "reject_lone_surrogate" })
        {
            Assert.Contains(rejects, c => c.GetProperty("gate").GetString() == gate);
        }

        var names = rejects.Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("lossy-int-9007199254740993", names);
        Assert.Contains("lossy-decimal-overlong", names);
        Assert.Contains("duplicate-key", names);
        Assert.Contains("duplicate-key-escaped-equal", names);
        Assert.Contains("lone-surrogate-value", names);
        Assert.Contains("lone-surrogate-name", names);
        Assert.Contains(Manifest.GetProperty("cases").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "cyberphone-values" && c.GetProperty("gate").GetString() == "reject_lossy_number");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_every_input_file_is_referenced_and_exists()
    {
        var referenced = Manifest.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("input").GetString()!).ToHashSet();
        foreach (var f in referenced)
        {
            Assert.True(File.Exists(Path.Combine(Dir, f)), f);
        }

        var onDisk = Directory.GetFiles(Dir, "*.input.json").Select(Path.GetFileName).ToHashSet();
        Assert.Equal(referenced.OrderBy(x => x, StringComparer.Ordinal), onDisk.OrderBy(x => x, StringComparer.Ordinal)!);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_canonical_files_have_no_bom_or_trailing_newline()
    {
        foreach (var f in Directory.GetFiles(Dir, "*.canonical.json"))
        {
            var b = File.ReadAllBytes(f);
            Assert.False(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, f);
            Assert.NotEqual((byte)'\n', b[^1]);
        }
    }
}
