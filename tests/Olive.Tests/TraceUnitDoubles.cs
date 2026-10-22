using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

public sealed class FakeTokenIssuer(params TokenIssueResult[] results) : ITokenIssuer
{
    private readonly Queue<TokenIssueResult> _queue = new(results);

    public int Calls { get; private set; }

    public void Enqueue(TokenIssueResult result) => _queue.Enqueue(result);

    public Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : new TokenIssueResult.Unavailable());
    }
}

public sealed class RecordingAlarm : IOperationalAlarm
{
    public List<string> Codes { get; } = [];

    public void Raise(string code, string component) => Codes.Add(code);
}

public sealed class FakeTraceResolver(params TraceLookup[] results) : ITraceResolver
{
    private readonly Queue<TraceLookup> _queue = new(results);

    public int Calls { get; private set; }

    public Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : new TraceLookup.Unavailable());
    }
}

public sealed class HangingTraceResolver : ITraceResolver
{
    public async Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new TraceLookup.Unavailable();
    }
}

public static class TraceBuilders
{
    public static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static ActiveKey Key(string keyId = "key-1", string agentId = "liam", Guid? tenant = null, DateTimeOffset? expires = null) =>
        new(keyId, "agent", agentId, null, "case_support", tenant ?? Tenant, "internal", expires ?? Start.AddHours(1));

    public static ServiceTokenGrant Grant(string keyId = "key-1", string agentId = "liam", Guid? tenant = null, DateTimeOffset? expires = null, string token = "tok-secret") =>
        new(new ServiceToken(token), tenant ?? Tenant, agentId, keyId, expires ?? Start.AddMinutes(30));

    public static TokenIssueResult Issued(ServiceTokenGrant? grant = null) => new TokenIssueResult.Issued(grant ?? Grant());

    public static TraceFacts Facts(string runId = "run-1", Guid? tenant = null, string agentId = "liam", string hash = "abc123", string schema = "ate/0.3") =>
        new("trace-1", schema, runId, tenant ?? Tenant, agentId, hash, Start);
}
