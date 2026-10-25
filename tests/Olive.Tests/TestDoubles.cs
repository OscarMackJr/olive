using System.Reflection;
using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

public sealed class RecordingKeyIntrospector(IKeyIntrospector inner, List<string> events, List<OutboundCall>? calls = null) : IKeyIntrospector
{
    private static readonly OutboundDestination Destination =
        typeof(IKeyIntrospector).GetCustomAttribute<OutboundPortAttribute>()!.Destination;

    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        lock (events) { events.Add("introspect"); }
        if (calls is not null)
        {
            lock (calls) { calls.Add(new OutboundCall(nameof(IKeyIntrospector), Destination, CarriedSessionKey: key is not null, CarriedServiceToken: false)); }
        }

        return await inner.IntrospectAsync(key, ct);
    }
}

// Not INonProductionOnly and not an IComponentDecorator: lets production positive controls run with fixture data, like RecordingKeyIntrospector.
public sealed class PassThroughServiceTokenVerifier(IServiceTokenVerifier inner) : IServiceTokenVerifier
{
    public Task<ServiceTokenResult> VerifyAsync(InboundBearerToken token, CancellationToken ct) => inner.VerifyAsync(token, ct);
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

    public Task<EvaluatedInsertResult?> InsertEvaluatedAsync(Guid tenantId, NewProposal proposal, int rateLimit, int windowSeconds, Olive.Domain.PolicyVerdict priorPassing, CancellationToken ct)
    {
        lock (events) { events.Add("insert"); }
        return inner.InsertEvaluatedAsync(tenantId, proposal, rateLimit, windowSeconds, priorPassing, ct);
    }

    public Task<Guid?> InsertRefusedAsync(Guid tenantId, NewProposal proposal, string refusalReason, RefusalKind kind, CancellationToken ct, string? policyDetailJson = null)
    {
        lock (events) { events.Add("insert_refused"); }
        return inner.InsertRefusedAsync(tenantId, proposal, refusalReason, kind, ct, policyDetailJson);
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
