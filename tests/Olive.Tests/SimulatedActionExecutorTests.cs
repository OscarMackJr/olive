using System.Text.Json;
using Olive.Application;
using Olive.Executor.Simulated;

namespace Olive.Tests;

public sealed class SimulatedActionExecutorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "olive-sim-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SimulatedActionExecutorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ExecutionRequest Request(Guid? id = null, string parameters = "{\"case_ref\":\"c1\",\"note_ref\":\"n1\"}") =>
        new(Guid.NewGuid(), id ?? Guid.NewGuid(), "crm.case_note@1", "nexus_crm", parameters, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static string[] ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task EXE_04_one_call_appends_one_line_with_exactly_the_allow_listed_properties()
    {
        var path = Path.Combine(_dir, "out.jsonl");
        var executor = new SimulatedActionExecutor(new SimulatedExecutorOptions(path));
        var req = Request();

        var result = await executor.ExecuteAsync(req, Ct);

        var line = Assert.Single(ReadLines(path));
        Assert.EndsWith("\n", File.ReadAllText(path));
        using var doc = JsonDocument.Parse(line);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["action_id", "executed_at", "execution_mode", "proposal_id", "target_system", "tenant_id", "would_send"], names);
        Assert.Equal("simulated", doc.RootElement.GetProperty("execution_mode").GetString());
        Assert.Equal(req.ProposalId, doc.RootElement.GetProperty("proposal_id").GetGuid());
        Assert.Equal(req.TenantId, doc.RootElement.GetProperty("tenant_id").GetGuid());
        Assert.Equal(req.ExecutedAt, doc.RootElement.GetProperty("executed_at").GetDateTimeOffset());
        Assert.Equal("c1", doc.RootElement.GetProperty("would_send").GetProperty("case_ref").GetString());
        Assert.Equal("simulated:" + req.ProposalId, result.ResultRef);
        Assert.DoesNotContain(_dir, result.ResultRef);
    }

    [Fact]
    public async Task EXE_04_decoy_property_in_parameters_appears_only_under_would_send()
    {
        var path = Path.Combine(_dir, "out.jsonl");
        var executor = new SimulatedActionExecutor(new SimulatedExecutorOptions(path));

        await executor.ExecuteAsync(Request(parameters: "{\"execution_mode\":\"live\",\"tenant_id\":\"decoy\"}"), Ct);

        using var doc = JsonDocument.Parse(Assert.Single(ReadLines(path)));
        Assert.Equal("simulated", doc.RootElement.GetProperty("execution_mode").GetString());
        Assert.Equal("live", doc.RootElement.GetProperty("would_send").GetProperty("execution_mode").GetString());
        Assert.Equal(7, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task EXE_04_twenty_concurrent_calls_give_twenty_intact_lines()
    {
        var path = Path.Combine(_dir, "out.jsonl");
        var executor = new SimulatedActionExecutor(new SimulatedExecutorOptions(path));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => executor.ExecuteAsync(Request(), Ct)));

        var lines = ReadLines(path);
        Assert.Equal(20, lines.Length);
        foreach (var l in lines) { using var d = JsonDocument.Parse(l); }
    }

    [Fact]
    public async Task EXE_04_unwritable_path_throws()
    {
        var executor = new SimulatedActionExecutor(new SimulatedExecutorOptions(_dir));

        await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(Request(), Ct));
    }
}
