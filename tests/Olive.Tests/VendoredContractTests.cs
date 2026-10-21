using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Olive.Application;

namespace Olive.Tests;

public sealed class VendoredContractTests
{
    private static string VocabularyPath => Path.Combine(TestPaths.Root, "contracts", "vendored", "vocabulary.yaml");

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_03_gitattributes_vendored_rule_is_last()
    {
        var lines = File.ReadAllLines(Path.Combine(TestPaths.Root, ".gitattributes"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        Assert.Equal("contracts/vendored/** -text", lines[^1]);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_03_vocabulary_has_lf_only()
    {
        var bytes = File.ReadAllBytes(VocabularyPath);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_03_vocabulary_sha256_matches_constant_and_vendored_md()
    {
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(VocabularyPath)));
        Assert.Equal(VendoredContracts.VocabularySha256, actual, ignoreCase: true);

        var md = File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "vendored", "VENDORED.md"));
        var match = Regex.Match(md, "SHA-256: `([0-9A-Fa-f]{64})`");
        Assert.True(match.Success, "VENDORED.md has no SHA-256 line");
        Assert.Equal(match.Groups[1].Value, actual, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_03_verify_rejects_tampered_or_missing_file()
    {
        Assert.True(VendoredContracts.VerifyVocabulary(VocabularyPath));

        var temp = Path.Combine(Path.GetTempPath(), "olive-vocab-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllBytes(temp, [.. File.ReadAllBytes(VocabularyPath), (byte)'\n']);
            Assert.False(VendoredContracts.VerifyVocabulary(temp));
        }
        finally
        {
            File.Delete(temp);
        }

        Assert.False(VendoredContracts.VerifyVocabulary(temp));
    }
}
