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

        var editorconfig = File.ReadAllText(Path.Combine(TestPaths.Root, ".editorconfig"));
        Assert.Contains("dotnet_diagnostic.RS0030.severity = error", editorconfig);

        var targets = File.ReadAllText(Path.Combine(TestPaths.Root, "Directory.Build.targets"));
        Assert.Contains("BannedSymbols.txt", targets);
        Assert.Contains("AdditionalFiles", targets);
    }
}
