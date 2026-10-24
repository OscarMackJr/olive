using System.Text.Json;
using Olive.Application;

namespace Olive.Executor.Simulated;

// Stands in for the target system (charter 3.3): appends one allow-listed JSONL line per call (D-10, D-11). Register as a singleton
// so the semaphore serialises appenders. Not INonProductionOnly: it is a labelled simulation, not a fixture (D-13).
public sealed class SimulatedActionExecutor(SimulatedExecutorOptions options) : IActionExecutor
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ExecutionTargetResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct)
    {
        using var parameters = JsonDocument.Parse(request.ParametersJson);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("proposal_id", request.ProposalId);
            w.WriteString("tenant_id", request.TenantId);
            w.WriteString("action_id", request.ActionId);
            w.WriteString("target_system", request.TargetSystem);
            w.WriteString("execution_mode", "simulated");
            w.WriteString("executed_at", request.ExecutedAt);
            w.WritePropertyName("would_send");
            parameters.RootElement.WriteTo(w);
            w.WriteEndObject();
        }

        buffer.WriteByte(10);
        var line = buffer.ToArray();

        await _gate.WaitAsync(ct);
        try
        {
            await using var fs = new FileStream(options.OutputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await fs.WriteAsync(line, ct);
            fs.Flush(flushToDisk: true);
        }
        finally
        {
            _gate.Release();
        }

        return new ExecutionTargetResult("simulated:" + request.ProposalId.ToString("D"));
    }
}
