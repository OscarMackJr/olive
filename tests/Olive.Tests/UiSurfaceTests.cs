using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Olive.Tests;

// UI-06: the reviewer UI is the minimal page set and nothing else (no dashboard, bulk, SLA or assignment; no script, inline style or CDN).
public sealed class UiSurfaceTests(PostgresFixture pg)
{
    private static readonly string HostRoot = Path.Combine(TestPaths.Root, "src", "Olive.Host");

    private static readonly string[] ExpectedPages =
    [
        "/", "/auth/failed", "/auth/sign-in", "/auth/sign-out", "/dev/sign-in", "/review", "/review/items/{id:guid}", "/review/items/{id:guid}/audit", "/signed-out",
    ];

    private static readonly string[] BannedWords = ["bulk", "dashboard", "sla", "assign", "export", "report"];

    private static string Normalize(string? raw)
    {
        var path = "/" + (raw ?? "").TrimStart('/');
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UI_06_the_only_non_api_routes_are_the_nine_reviewer_pages()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var _ = factory.CreateUiClient();
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Select(e => Normalize(e.RoutePattern.RawText))
            .Where(p => !p.StartsWith("/v1/", StringComparison.Ordinal))
            .Distinct().Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(ExpectedPages.Order(StringComparer.Ordinal).ToArray(), routes);
        foreach (var route in routes)
        {
            foreach (var word in BannedWords) { Assert.DoesNotContain(word, route, StringComparison.OrdinalIgnoreCase); }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_06_no_page_or_asset_file_name_mentions_a_banned_feature()
    {
        var files = Directory.EnumerateFiles(Path.Combine(HostRoot, "Pages"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(HostRoot, "wwwroot"), "*", SearchOption.AllDirectories)).ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            foreach (var word in BannedWords) { Assert.False(name.Contains(word, StringComparison.OrdinalIgnoreCase), "banned feature word in file name " + name); }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_06_pages_carry_no_script_inline_style_raw_html_or_third_party_url()
    {
        var pages = Directory.EnumerateFiles(Path.Combine(HostRoot, "Pages"), "*.cshtml*", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(pages);
        foreach (var file in pages)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetRelativePath(HostRoot, file);
            Assert.False(Regex.IsMatch(text, "<script", RegexOptions.IgnoreCase), "<script in " + name);
            Assert.False(Regex.IsMatch(text, "style\\s*=", RegexOptions.IgnoreCase), "inline style in " + name);
            Assert.False(text.Contains("Html.Raw", StringComparison.Ordinal), "Html.Raw in " + name);
            Assert.False(Regex.IsMatch(text, "https?://", RegexOptions.IgnoreCase), "absolute URL in " + name);
        }

        var assets = Directory.EnumerateFiles(Path.Combine(HostRoot, "wwwroot"), "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(["olive.css"], assets.Select(a => Path.GetRelativePath(Path.Combine(HostRoot, "wwwroot"), a).Replace('\\', '/')).ToArray());
        Assert.DoesNotMatch("https?://|@import", File.ReadAllText(assets[0]));
        var layout = File.ReadAllText(Path.Combine(HostRoot, "Pages", "Shared", "_Layout.cshtml"));
        Assert.Single(Regex.Matches(layout, "<link[^>]*stylesheet", RegexOptions.IgnoreCase));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_06_page_models_take_no_database_or_http_types()
    {
        var forbiddenNamespaces = new[] { "Npgsql", "Dapper", "System.Data", "Olive.Persistence", "System.Net.Http" };
        var pageModels = typeof(Olive.Host.Pages.Shared.FlashMessages).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Olive.Host.Pages", StringComparison.Ordinal) == true && typeof(Microsoft.AspNetCore.Mvc.RazorPages.PageModel).IsAssignableFrom(t))
            .ToArray();
        Assert.True(pageModels.Length >= 8, "expected the reviewer page models, found " + pageModels.Length);
        foreach (var type in pageModels)
        {
            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                var ns = parameter.ParameterType.Namespace ?? "";
                Assert.False(forbiddenNamespaces.Any(f => ns.StartsWith(f, StringComparison.Ordinal)), type.Name + " takes " + parameter.ParameterType.FullName);
            }
        }

        // The item page reads the proposal for display only: GetAsync is the single IProposalStore member any page may call.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(HostRoot, "Pages"), "*.cshtml.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotMatch("\\bproposals\\.(?!GetAsync)", File.ReadAllText(file));
        }
    }
}
