using System.Reflection;
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
        ["Olive.Catalog"] = ["JsonSchema.Net", "YamlDotNet"],
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

    // ---- IDN-08 port graph (ROADMAP SC5, Q19): which credential types each outbound port may carry. ----

    private static readonly Type[] SessionKeyTypes = [typeof(Olive.Domain.SessionKey)];
    private static readonly Type[] ServiceTokenTypes = [typeof(Olive.Domain.ServiceToken), typeof(Olive.Application.ServiceTokenGrant)];

    private static IEnumerable<Type> OutboundPorts() =>
        typeof(Olive.Application.AssemblyMarker).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.GetCustomAttributes(typeof(Olive.Application.OutboundPortAttribute), false).Length > 0);

    private static IEnumerable<Type> Flatten(Type t)
    {
        yield return t;
        if (t.HasElementType) { foreach (var e in Flatten(t.GetElementType()!)) { yield return e; } }
        if (t.IsGenericType) { foreach (var a in t.GetGenericArguments().SelectMany(Flatten)) { yield return a; } }
    }

    // Parameter types and the generic arguments of return types (for example Task<T>), flattened.
    private static IEnumerable<Type> SignatureTypes(Type port) =>
        port.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)).SelectMany(Flatten);

    private static Olive.Application.OutboundDestination DestinationOf(Type port) =>
        ((Olive.Application.OutboundPortAttribute)port.GetCustomAttributes(typeof(Olive.Application.OutboundPortAttribute), false)[0]).Destination;

    [Fact]
    [Trait("Category", "Architecture")]
    public void IDN_08_only_popeye_ports_accept_a_session_key()
    {
        var ports = OutboundPorts().ToArray();
        Assert.NotEmpty(ports);
        foreach (var port in ports)
        {
            var carriesKey = SignatureTypes(port).Any(t => SessionKeyTypes.Contains(t));
            if (DestinationOf(port) != Olive.Application.OutboundDestination.Popeye)
            {
                Assert.False(carriesKey, $"{port.Name} is not a popeye port but its signature mentions SessionKey");
            }
        }

        // Non-vacuous: popeye ports really do take the key.
        Assert.Contains(ports, p => DestinationOf(p) == Olive.Application.OutboundDestination.Popeye && SignatureTypes(p).Any(t => SessionKeyTypes.Contains(t)));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void IDN_08_only_hometown_ports_accept_a_service_token_or_grant()
    {
        var ports = OutboundPorts().ToArray();
        foreach (var port in ports)
        {
            var parameterTypes = port.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType)).SelectMany(Flatten);
            if (DestinationOf(port) != Olive.Application.OutboundDestination.Hometown)
            {
                Assert.False(parameterTypes.Any(t => ServiceTokenTypes.Contains(t)), $"{port.Name} is not a hometown port but accepts a service token or grant");
            }
        }

        Assert.Contains(ports, p => DestinationOf(p) == Olive.Application.OutboundDestination.Hometown
            && p.GetMethods().SelectMany(m => m.GetParameters()).Any(x => ServiceTokenTypes.Contains(x.ParameterType)));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void IDN_08_every_application_port_implemented_in_an_integration_assembly_is_marked_outbound()
    {
        var appAssembly = typeof(Olive.Application.AssemblyMarker).Assembly;
        // Application interfaces that are not ports: they describe component wiring, not an outbound call.
        var nonPorts = new Dictionary<string, string>
        {
            [nameof(Olive.Application.IComponentDecorator)] = "wiring marker that exposes a wrapped component",
            [nameof(Olive.Application.INonProductionOnly)] = "wiring marker for the FND-05 production guard",
        };
        Assembly[] integrations =
        [
            typeof(Olive.Integrations.Popeye.AssemblyMarker).Assembly,
            typeof(Olive.Integrations.Hometown.AssemblyMarker).Assembly,
            typeof(Olive.Integrations.Entra.AssemblyMarker).Assembly,
        ];
        var implemented = 0;
        foreach (var asm in integrations)
        {
            foreach (var type in asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
            {
                foreach (var iface in type.GetInterfaces().Where(i => i.Assembly == appAssembly && !nonPorts.ContainsKey(i.Name)))
                {
                    implemented++;
                    Assert.True(iface.GetCustomAttributes(typeof(Olive.Application.OutboundPortAttribute), false).Length > 0,
                        $"{type.Name} implements {iface.Name}, which is not marked [OutboundPort]");
                }
            }
        }

        Assert.True(implemented > 0, "no integration implementation found");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void POL_02_directory_port_carries_no_session_key_or_service_token()
    {
        var port = typeof(Olive.Application.IDirectoryGroupResolver);
        Assert.Equal(Olive.Application.OutboundDestination.Directory, DestinationOf(port));
        var types = SignatureTypes(port).ToArray();
        Assert.DoesNotContain(types, t => SessionKeyTypes.Contains(t) || ServiceTokenTypes.Contains(t));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void IDN_08_hometown_project_sources_never_reference_session_key()
    {
        var files = Directory.GetFiles(Path.Combine(TestPaths.Root, "src", "Olive.Integrations.Hometown"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        Assert.NotEmpty(files);
        var hits = files.SelectMany(f => File.ReadAllLines(f)
                .Select(l => l.TrimStart())
                .Where(l => !(l.StartsWith("//", StringComparison.Ordinal) || l.StartsWith("/*", StringComparison.Ordinal) || l.StartsWith('*')))
                .Where(l => l.Contains("SessionKey", StringComparison.Ordinal))
                .Select(_ => Path.GetFileName(f)))
            .ToArray();
        Assert.True(hits.Length == 0, "SessionKey referenced in hometown sources: " + string.Join(",", hits));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void IDN_08_resolver_port_has_exactly_grant_traceid_and_cancellation_parameters()
    {
        var methods = typeof(Olive.Application.ITraceResolver).GetMethods();
        var m = Assert.Single(methods);
        Assert.Equal("ResolveAsync", m.Name);
        Assert.Equal(
            [(typeof(Olive.Application.ServiceTokenGrant), "grant"), (typeof(string), "traceId"), (typeof(CancellationToken), "ct")],
            m.GetParameters().Select(p => (p.ParameterType, p.Name!)).ToArray());
    }
}
