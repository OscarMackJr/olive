using System.Reflection;
using Olive.Application;

namespace Olive.Tests;

public sealed class SourceHygieneTests
{
    private static readonly string[] OliveAssemblyNames =
    [
        "Olive.Domain", "Olive.Application", "Olive.Persistence", "Olive.Catalog", "Olive.Executor.Simulated",
        "Olive.Integrations.Popeye", "Olive.Integrations.Hometown", "Olive.Integrations.Entra", "Olive.Host",
    ];

    private static IEnumerable<Assembly> OliveAssemblies() => OliveAssemblyNames.Select(Assembly.Load);

    private static IEnumerable<Type> AllTypes() =>
        OliveAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); } });

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_06_expose_secret_called_only_in_popeye_and_secret_types()
    {
        var src = Path.Combine(TestPaths.Root, "src");
        var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("ExposeSecret(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("Olive.Integrations.Popeye/", StringComparison.Ordinal)
                        && f != "Olive.Domain/SessionKey.cs" && f != "Olive.Domain/ServiceToken.cs")
            .ToList();
        // AgentKeyHeader mentions no ExposeSecret( call; any offender here is a leak path.
        Assert.Empty(offenders);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_06_no_record_type_holds_secret()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var secretNames = new[] { "SessionKey", "ServiceToken" };
        var offenders = new List<string>();
        foreach (var t in AllTypes().Where(t => t.GetMethod("<Clone>$", all) is not null))
        {
            var memberTypes = t.GetFields(all).Select(f => f.FieldType)
                .Concat(t.GetProperties(all).Select(p => p.PropertyType));
            if (memberTypes.Any(mt => secretNames.Contains(mt.Name)))
            {
                offenders.Add(t.FullName!);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void FND_05_stub_types_implement_non_production_marker()
    {
        var offenders = AllTypes()
            .Where(t => t.Name.StartsWith("Stub", StringComparison.Ordinal) || t.Name.StartsWith("Fixture", StringComparison.Ordinal))
            .Where(t => !typeof(INonProductionOnly).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(offenders);
        Assert.Contains(AllTypes(), t => t.Name == "StubKeyIntrospector");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void IDN_02_allowlist_source_does_not_read_environment()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Olive.Catalog", "AgentAllowlist.cs"));
        Assert.DoesNotContain("GetEnvironmentVariable", text);
        Assert.DoesNotContain("IConfiguration", text);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void IDN_02_yamldotnet_confined_to_catalog_and_header_name_has_one_owner()
    {
        var src = Path.Combine(TestPaths.Root, "src");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(f => (Rel: Path.GetRelativePath(src, f).Replace('\\', '/'), Text: File.ReadAllText(f)))
            .ToList();
        Assert.All(files.Where(f => f.Text.Contains("YamlDotNet")), f => Assert.StartsWith("Olive.Catalog/", f.Rel));
        Assert.All(files.Where(f => f.Text.Contains("\"Authorization\"")), f => Assert.Equal("Olive.Application/AgentKeyHeader.cs", f.Rel));
    }
}
