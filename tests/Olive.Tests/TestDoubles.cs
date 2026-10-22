using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

public sealed class RecordingKeyIntrospector(IKeyIntrospector inner, List<string> events) : IKeyIntrospector
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (events) { events.Add("introspect"); }
        return await inner.IntrospectAsync(key, ct);
    }
}

public sealed class ThrowingKeyIntrospector : IKeyIntrospector
{
    public Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct) =>
        throw new IntrospectionUnavailableException("test_unavailable");
}

public sealed class RecordingProposalStore(IProposalStore inner, List<string> events) : IProposalStore
{
    public Task<Guid?> InsertAsync(Guid tenantId, NewProposal proposal, CancellationToken ct)
    {
        lock (events) { events.Add("insert"); }
        return inner.InsertAsync(tenantId, proposal, ct);
    }

    public Task<ProposalRecord?> GetAsync(Guid tenantId, Guid proposalId, CancellationToken ct)
    {
        lock (events) { events.Add("get"); }
        return inner.GetAsync(tenantId, proposalId, ct);
    }

    public Task<ProposalRunRecord?> FindByRunAsync(Guid tenantId, string agentId, string runId, CancellationToken ct)
    {
        lock (events) { events.Add("FindByRun"); }
        return inner.FindByRunAsync(tenantId, agentId, runId, ct);
    }

    public Task<bool> TransitionAsync(Guid tenantId, Guid proposalId, string fromState, string toState, string actor, CancellationToken ct)
    {
        lock (events) { events.Add("transition"); }
        return inner.TransitionAsync(tenantId, proposalId, fromState, toState, actor, ct);
    }
}
