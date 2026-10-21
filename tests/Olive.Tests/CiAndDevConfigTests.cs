using Olive.Application;
using Olive.Host.Startup;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class CiAndDevConfigTests
{
    private static string Workflow => File.ReadAllText(Path.Combine(TestPaths.Root, ".github", "workflows", "ci.yml"));

    private static string EnvExample => File.ReadAllText(Path.Combine(TestPaths.Root, ".env.example"));

    [Fact]
    public void FND_07_workflow_runs_on_ubuntu_with_checkout_and_global_json_sdk()
    {
        var w = Workflow;
        Assert.Contains("runs-on: ubuntu-latest", w);
        Assert.Contains("actions/checkout", w);
        Assert.Contains("actions/setup-dotnet", w);
        Assert.Contains("global-json-file: global.json", w);
        Assert.Contains("contents: read", w);
    }

    [Fact]
    public void FND_07_workflow_restores_locked_then_builds_release_then_tests_without_rebuild()
    {
        var w = Workflow;
        var restore = w.IndexOf("dotnet restore --locked-mode", StringComparison.Ordinal);
        var build = w.IndexOf("dotnet build -c Release --no-restore", StringComparison.Ordinal);
        var test = w.IndexOf("dotnet test -c Release --no-build", StringComparison.Ordinal);
        Assert.True(restore >= 0 && build > restore && test > build);
        Assert.DoesNotContain("TESTCONTAINERS_RYUK_DISABLED", w);
    }

    [Fact]
    public void FND_07_workflow_vocabulary_hash_equals_code_constant_and_precedes_restore()
    {
        var w = Workflow;
        Assert.Contains("sha256sum -c", w);
        Assert.Contains(VendoredContracts.VocabularySha256 + "  contracts/vendored/vocabulary.yaml", w);
        Assert.True(w.IndexOf("sha256sum -c", StringComparison.Ordinal) < w.IndexOf("dotnet restore", StringComparison.Ordinal));
    }

    [Fact]
    public void FND_07_workflow_fails_on_vulnerable_packages()
    {
        var w = Workflow;
        Assert.Contains("dotnet list package --vulnerable --include-transitive", w);
        Assert.Contains("has the following vulnerable packages", w);
        Assert.Contains("exit 1", w);
    }

    [Fact]
    public void FND_07_workflow_uses_only_first_party_actions()
    {
        foreach (var line in Workflow.Split('\n').Where(l => l.Contains("uses:", StringComparison.Ordinal)))
        {
            Assert.Contains("uses: actions/", line);
        }
    }

    [Fact]
    public void FND_04_env_example_parses_with_in_house_loader_and_has_documented_variables()
    {
        var d = DotEnvFile.Parse(EnvExample);
        Assert.Equal("local", d["OLIVE_ENVIRONMENT"]);
        Assert.StartsWith("postgresql://olive_app:", d["OLIVE_POSTGRES_URL"]);
        Assert.Equal("5432", d["OLIVE_POSTGRES_PORT"]);
        Assert.Equal("olive", d["OLIVE_POSTGRES_DB"]);
        Assert.Equal("olive_app", d["OLIVE_POSTGRES_USER"]);
        Assert.True(d.ContainsKey("OLIVE_POSTGRES_HOST"));
        Assert.True(d.ContainsKey("OLIVE_POSTGRES_PASSWORD"));
    }

    [Fact]
    public void FND_04_env_example_migrator_url_appears_only_in_comments_and_password_is_placeholder()
    {
        var text = EnvExample;
        var parsed = DotEnvFile.Parse(text);
        Assert.False(parsed.ContainsKey("OLIVE_MIGRATOR_URL"));
        foreach (var line in text.Split('\n').Where(l => l.Contains("OLIVE_MIGRATOR_URL", StringComparison.Ordinal)))
        {
            Assert.StartsWith("#", line.TrimStart());
        }

        Assert.Contains("CHANGE_ME", parsed["OLIVE_POSTGRES_URL"]);
    }

    [Fact]
    public void FND_07_lockfiles_are_covered_by_lf_gitattributes()
    {
        var ga = File.ReadAllText(Path.Combine(TestPaths.Root, ".gitattributes"));
        Assert.Contains("packages.lock.json text eol=lf", ga);
    }
}
