using Microsoft.Extensions.Configuration;
using Olive.Host.Startup;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class DotEnvTests
{
    [Fact]
    public void FND_04_dotenv_parses_pairs_comments_and_quotes()
    {
        var d = DotEnvFile.Parse("# comment\n\nA=1\nB=\"two words\"\nC='x'\r\nD=a=b=c\n");
        Assert.Equal("1", d["A"]);
        Assert.Equal("two words", d["B"]);
        Assert.Equal("x", d["C"]);
        Assert.Equal("a=b=c", d["D"]);
        Assert.Equal(4, d.Count);
    }

    [Theory]
    [InlineData("lower=1")]
    [InlineData("=novalue")]
    [InlineData("noequals")]
    [InlineData("1BAD=1")]
    public void FND_04_dotenv_invalid_line_names_line_number_only(string line)
    {
        var e = Assert.Throws<FormatException>(() => DotEnvFile.Parse("OK=1\n" + line + "\n"));
        Assert.Contains("line 2", e.Message);
        Assert.DoesNotContain(line, e.Message);
    }

    [Fact]
    public void FND_04_dotenv_secret_value_is_not_echoed_on_error()
    {
        var e = Assert.Throws<FormatException>(() => DotEnvFile.Parse("bad key=supersecretvalue"));
        Assert.DoesNotContain("supersecretvalue", e.Message);
    }

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "olive-env-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void FND_04_dotenv_real_sources_win_and_dotenv_only_keys_are_visible()
    {
        var path = WriteTemp("OLIVE_ENVIRONMENT=local\nONLY_IN_ENV_FILE=yes\n");
        try
        {
            var cfg = new ConfigurationManager();
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["OLIVE_ENVIRONMENT"] = "ci" });
            cfg.AddDotEnvBelowEnvironment(path);
            Assert.Equal("ci", cfg["OLIVE_ENVIRONMENT"]);
            Assert.Equal("yes", cfg["ONLY_IN_ENV_FILE"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FND_04_dotenv_missing_file_is_ignored()
    {
        var cfg = new ConfigurationManager();
        cfg.AddDotEnvBelowEnvironment(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")));
        Assert.Null(cfg["OLIVE_ENVIRONMENT"]);
    }

    [Fact]
    public void FND_04_dotenv_migrator_url_in_env_file_is_refused_by_guard()
    {
        var path = WriteTemp("OLIVE_MIGRATOR_URL=postgresql://u:topsecret@h/db\n");
        try
        {
            var cfg = new ConfigurationManager();
            cfg.AddDotEnvBelowEnvironment(path);
            var e = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckMigratorUrlAbsent(cfg));
            Assert.Contains("D-19", e.Message);
            Assert.DoesNotContain("topsecret", e.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
