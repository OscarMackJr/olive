using System.Text.RegularExpressions;

namespace Olive.Persistence;

public static partial class MigrationCatalog
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

        ValidateFileNames(names.Select(n => n[Prefix.Length..]).ToList());

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

    // DB-02 shape (WR-07): NNNN_snake_case.sql, unique versions, contiguous from 0001, so ordinal order is apply order.
    public static void ValidateFileNames(IReadOnlyList<string> fileNames)
    {
        if (fileNames.Any(f => !NamePattern().IsMatch(f)))
        {
            throw new InvalidOperationException("Invalid migration file name.");
        }

        var versions = fileNames.Select(f => int.Parse(f.AsSpan(0, 4), System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        if (versions.Distinct().Count() != versions.Count)
        {
            throw new InvalidOperationException("Duplicate migration version.");
        }

        for (var i = 0; i < versions.Count; i++)
        {
            if (versions[i] != i + 1)
            {
                throw new InvalidOperationException("Migration versions are not contiguous from 0001.");
            }
        }
    }

    [GeneratedRegex("^[0-9]{4}_[a-z0-9_]+\\.sql$")]
    private static partial Regex NamePattern();
}
