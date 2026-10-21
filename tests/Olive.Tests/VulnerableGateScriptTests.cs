using System.Diagnostics;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class VulnerableGateScriptTests
{
    private const string Clean = "The given project `{0}` has no vulnerable packages given the current sources.";
    private const string Vulnerable = "Project `{0}` has the following vulnerable packages\n   [net10.0]: Top-level Package Requested Resolved Severity";

    private static string? ResolveBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "bash";
        }

        var candidates = new List<string>();
        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
        {
            if (string.IsNullOrEmpty(pf))
            {
                continue;
            }

            candidates.Add(Path.Combine(pf, "Git", "bin", "bash.exe"));
            candidates.Add(Path.Combine(pf, "Git", "usr", "bin", "bash.exe"));
        }

        var exec = RunCapture("git", ["--exec-path"]);
        if (!string.IsNullOrWhiteSpace(exec))
        {
            var dir = new DirectoryInfo(exec.Trim().Replace('/', Path.DirectorySeparatorChar));
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "bin", "bash.exe")))
            {
                dir = dir.Parent;
            }

            if (dir is not null)
            {
                candidates.Add(Path.Combine(dir.FullName, "bin", "bash.exe"));
            }
        }

        return candidates.FirstOrDefault(c => File.Exists(c) && !c.Contains("System32", StringComparison.OrdinalIgnoreCase));
    }

    private static string? RunCapture(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return o;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Fwd(string path) => path.Replace('\\', '/');

    private static int RunBash(string bash, params string[] args)
    {
        var psi = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static int Run(string? report, string expected, string? dotnetExit = null, bool writeReport = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "olive-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var reportPath = Path.Combine(dir, "vulnerable.txt");
            if (writeReport)
            {
                File.WriteAllText(reportPath, report ?? "");
            }

            var bash = ResolveBash();
            if (bash is null)
            {
                Assert.Skip("bash not available");
                return -1;
            }

            if (writeReport && RunBash(bash, "-c", "test -r " + Fwd(reportPath)) != 0)
            {
                Assert.Skip("bash cannot read Windows temp paths");
                return -1;
            }

            var script = Fwd(Path.Combine(TestPaths.Root, "build", "vulnerable-gate.sh"));
            var args = new List<string> { script, Fwd(reportPath), expected };
            if (dotnetExit is not null)
            {
                args.Add(dotnetExit);
            }

            return RunBash(bash, args.ToArray());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string Lines(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i => string.Format(Clean, "P" + i + ".csproj"))) + "\n";

    [Fact]
    public void FND_07_gate_script_all_clean_matching_count_exits_0() => Assert.Equal(0, Run(Lines(3), "3", "0"));

    [Fact]
    public void FND_07_gate_script_one_vulnerable_exits_1() =>
        Assert.Equal(1, Run(Lines(2) + string.Format(Vulnerable, "Bad.csproj") + "\n", "3", "0"));

    [Fact]
    public void FND_07_gate_script_empty_report_exits_1() => Assert.Equal(1, Run("", "3", "0"));

    [Fact]
    public void FND_07_gate_script_missing_project_exits_1() => Assert.Equal(1, Run(Lines(2), "3", "0"));

    [Fact]
    public void FND_07_gate_script_extra_unexpected_count_exits_1() => Assert.Equal(1, Run(Lines(3), "2", "0"));

    [Fact]
    public void FND_07_gate_script_dotnet_nonzero_exit_code_exits_1() => Assert.Equal(1, Run(Lines(3), "3", "1"));

    [Fact]
    public void FND_07_gate_script_non_numeric_expected_count_exits_1() => Assert.Equal(1, Run(Lines(3), "abc", "0"));

    [Fact]
    public void FND_07_gate_script_zero_expected_count_exits_1() => Assert.Equal(1, Run(Lines(3), "0", "0"));

    [Fact]
    public void FND_07_gate_script_missing_report_file_exits_1() => Assert.Equal(1, Run(null, "3", "0", writeReport: false));
}
