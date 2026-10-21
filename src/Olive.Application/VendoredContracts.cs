using System.Security.Cryptography;

namespace Olive.Application;

public static class VendoredContracts
{
    public const string VocabularySha256 = "60a44f00db0daafcad3a4e11fe8fcbb2821e1ca7879302bae413b212a0c1e8e7";

    public static bool VerifyVocabulary(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return string.Equals(actual, VocabularySha256, StringComparison.OrdinalIgnoreCase);
    }
}
