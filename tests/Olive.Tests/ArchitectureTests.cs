using System.Text.Json;
using System.Xml.Linq;

namespace Olive.Tests;

// These tests supersede the retired scaffold test REQ_ACT_04_no_external_write_connector_exists (research decision Q8).
public sealed class ArchitectureTests
{
    private static readonly Dictionary<string, string[]> ReferenceAllowlist = new()
    {
        ["Olive.Domain"] = [],
        ["Olive.Contracts"] = [],
        ["Olive.Application"] = ["Olive.Domain"],
        ["Olive.Persistence"] = ["Olive.Application", "Olive.Domain"],
        ["Olive.Catalog"] = ["Olive.Application", "Olive.Domain"],
        ["Olive.Executor.Simulated"] = ["Olive.Application", "Olive.Domain"],
        ["Olive.Integrations.Popeye"] = ["Olive.Application", "Olive.Domain"],
        ["Olive.Integrations.Hometown"] = ["Olive.Application", "Olive.Domain"],
        ["Olive.Integrations.Entra"] = ["Olive.Application", "Olive.Domain"],
        ["Olive.Host"] =
        [
            "Olive.Application", "Olive.Domain", "Olive.Contracts", "Olive.Persistence", "Olive.Catalog",
            "Olive.Executor.Simulated", "Olive.Integrations.Popeye", "Olive.Integrations.Hometown",
            "Olive.Integrations.Entra",
        ],
        ["Olive.Migrator"] = ["Olive.Persistence"],
    };

    private static readonly Dictionary<string, string[]> PackageAllowlist = new()
    {
        ["Olive.Persistence"] = ["Dapper", "Npgsql"],
        ["Olive.Catalog"] = ["YamlDotNet"],
        ["Olive.Host"] = ["Npgsql"],
    };

    private static string[] SrcProjectDirs() =>
        Directory.GetDirectories(Path.Combine(TestPaths.Root, "src")).OrderBy(d => d, StringComparer.Ordinal).ToArray();

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_01_project_references_match_inward_graph()
    {
        foreach (var dir in SrcProjectDirs())
        {
            var name = Path.GetFileName(dir);
            var csproj = XDocument.Load(Path.Combine(dir, name + ".csproj"));
            var actual = csproj.Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            var expected = ReferenceAllowlist[name].OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.True(expected.SequenceEqual(actual), $"{name}: expected [{string.Join(",", expected)}] but was [{string.Join(",", actual)}]");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_01_exactly_eleven_src_projects()
    {
        var names = SrcProjectDirs().Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(ReferenceAllowlist.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray(), names);
        Assert.Equal(11, names.Length);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_02_domain_application_executor_do_not_reference_http_assemblies()
    {
        var assemblies = new[]
        {
            typeof(Olive.Domain.AssemblyMarker).Assembly,
            typeof(Olive.Application.AssemblyMarker).Assembly,
            typeof(Olive.Executor.Simulated.AssemblyMarker).Assembly,
        };
        foreach (var assembly in assemblies)
        {
            var http = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? "")
                .Where(n => n.StartsWith("System.Net.Http", StringComparison.Ordinal))
                .ToArray();
            Assert.True(http.Length == 0, $"{assembly.GetName().Name} references {string.Join(",", http)}");
        }
    }

    // Olive's own code in every src project (not Npgsql's, which is a separate assembly) must not reference
    // outbound HTTP or raw socket assemblies.
    [Fact]
    [Trait("Category", "Architecture")]
    public void REQ_ACT_04_no_src_assembly_references_outbound_network_assemblies()
    {
        string[] bannedPrefixes = ["System.Net.Http", "System.Net.Sockets", "System.Net.Security", "System.Net.WebClient", "System.Net.Requests"];
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var (configuration, framework) = (baseDir.Parent!.Name, baseDir.Name);
        foreach (var dir in SrcProjectDirs())
        {
            var name = Path.GetFileName(dir);
            // Not every src project is referenced by the tests (the Migrator is not), so read the built file's metadata.
            var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (!File.Exists(path))
            {
                path = Path.Combine(dir, "bin", configuration, framework, name + ".dll");
            }

            Assert.True(File.Exists(path), $"{name}: built assembly not found");
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var refs = md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .Where(n => bannedPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .ToArray();
            Assert.True(refs.Length == 0, $"{name} references {string.Join(",", refs)}");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_02_direct_packages_match_allowlist_in_lockfiles()
    {
        foreach (var dir in SrcProjectDirs())
        {
            var name = Path.GetFileName(dir);
            var lockPath = Path.Combine(dir, "packages.lock.json");
            Assert.True(File.Exists(lockPath), $"{name}: packages.lock.json missing");

            using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
            var direct = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var framework in doc.RootElement.GetProperty("dependencies").EnumerateObject())
            {
                foreach (var package in framework.Value.EnumerateObject())
                {
                    if (package.Value.GetProperty("type").GetString() == "Direct"
                        && package.Name != "Microsoft.CodeAnalysis.BannedApiAnalyzers")
                    {
                        direct.Add(package.Name);
                    }
                }
            }

            var expected = PackageAllowlist.TryGetValue(name, out var allowed) ? allowed : [];
            Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal).ToArray(), direct.ToArray());
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_02_banned_api_configuration_is_error_level()
    {
        var banned = File.ReadAllText(Path.Combine(TestPaths.Root, "BannedSymbols.txt"));
        Assert.Contains("T:System.Net.Http.HttpClient;", banned);
        Assert.Contains("T:System.Net.Http.HttpClientHandler;", banned);
        Assert.Contains("T:System.Net.Http.SocketsHttpHandler;", banned);
        Assert.Contains("T:System.Net.WebClient;", banned);
        Assert.Contains("T:System.Net.Http.HttpMessageInvoker;", banned);
        Assert.Contains("T:System.Net.Sockets.TcpClient;", banned);
        Assert.Contains("T:System.Net.Sockets.Socket;", banned);
        Assert.Contains("T:System.Net.Sockets.UdpClient;", banned);
        Assert.Contains("T:System.Net.Security.SslStream;", banned);

        var editorconfig = File.ReadAllText(Path.Combine(TestPaths.Root, ".editorconfig"));
        Assert.Contains("dotnet_diagnostic.RS0030.severity = error", editorconfig);

        var targets = File.ReadAllText(Path.Combine(TestPaths.Root, "Directory.Build.targets"));
        Assert.Contains("BannedSymbols.txt", targets);
        Assert.Contains("AdditionalFiles", targets);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void DB_02_migration_files_on_disk_have_valid_names_and_contiguous_versions()
    {
        var names = Directory.GetFiles(Path.Combine(TestPaths.Root, "sql", "migrations"))
            .Select(f => Path.GetFileName(f)!)
            .ToList();
        Assert.NotEmpty(names);
        Olive.Persistence.MigrationCatalog.ValidateFileNames(names);
    }

    [Theory]
    [Trait("Category", "Architecture")]
    [InlineData("0001.sql")]
    [InlineData("1_init.sql")]
    [InlineData("0001_Init.sql")]
    [InlineData("0001_init.SQL")]
    [InlineData("0001-init.sql")]
    public void DB_02_migration_file_name_shape_is_enforced(string bad)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Olive.Persistence.MigrationCatalog.ValidateFileNames(["0001_init.sql", bad]));
        Assert.Equal("Invalid migration file name.", e.Message);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void DB_02_duplicate_migration_versions_are_refused()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Olive.Persistence.MigrationCatalog.ValidateFileNames(["0001_init.sql", "0002_a.sql", "0002_b.sql"]));
        Assert.Equal("Duplicate migration version.", e.Message);
    }

    [Theory]
    [Trait("Category", "Architecture")]
    [InlineData("0002_init.sql")]
    [InlineData("0001_init.sql", "0003_gap.sql")]
    public void DB_02_non_contiguous_migration_versions_are_refused(params string[] names)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Olive.Persistence.MigrationCatalog.ValidateFileNames(names));
        Assert.Contains("contiguous", e.Message);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void DB_02_migration_file_without_underscore_has_no_version()
    {
        var file = new Olive.Persistence.MigrationFile("0002.sql", []);
        Assert.Throws<InvalidOperationException>(() => file.Version);
    }
}
