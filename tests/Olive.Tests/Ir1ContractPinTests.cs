using System.Security.Cryptography;

namespace Olive.Tests;

public sealed class Ir1ContractPinTests
{
    private const string RatifiedSha256 = "c993c35e261bc38e3cb5ca24e54ba55c6e5f8c2f9be277e8ff5b2ef1cabff5cf";

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_01_ir1_contract_file_is_byte_identical_to_the_ratified_hash()
    {
        var path = Path.Combine(TestPaths.Root, "contracts", "queue", "identity-review", "QUEUE_ITEM_CONTRACT_identity-review_v0.1.md");
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(actual == RatifiedSha256, "IR-1 v0.1 contract file changed; it is pinned by bluto and needs both owners' agreement.");
    }
}
