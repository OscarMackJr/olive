using System.Security.Cryptography;
using System.Text;

namespace Olive.Persistence;

public sealed record MigrationFile(string FileName, byte[] Bytes)
{
    public string Version => FileName.IndexOf('_') is > 0 and var i ? FileName[..i] : throw new InvalidOperationException("Invalid migration file name.");

    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Bytes));

    public string Sql => new UTF8Encoding(false).GetString(Bytes);
}

public sealed class MigrationChecksumException(string version)
    : Exception("Applied migration file changed: " + version);

public sealed class MigrationDriftException(string version)
    : Exception("Database has a migration not present in this build: " + version);
