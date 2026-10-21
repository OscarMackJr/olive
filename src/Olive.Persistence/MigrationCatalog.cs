namespace Olive.Persistence;

public static class MigrationCatalog
{
    private const string Prefix = "migrations/";

    public static IReadOnlyList<MigrationFile> LoadEmbedded()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException("No embedded migrations found.");
        }

        var files = new List<MigrationFile>();
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            files.Add(new MigrationFile(name[Prefix.Length..], ms.ToArray()));
        }

        return files;
    }
}
