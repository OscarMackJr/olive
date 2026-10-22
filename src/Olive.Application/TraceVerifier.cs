using Olive.Domain;

namespace Olive.Application;

// D17.3: run, tenant, agent, proposal hash, in that order; first failure wins. Never hashes, never logs.
public sealed class TraceVerifier(ServiceTokenProvider tokens, ITraceResolver resolver, IOperationalAlarm alarm, TraceVerifierOptions options)
{
    private const string Component = "hometown";

    public async Task<TraceVerdict> VerifyAsync(SessionKey sessionKey, ActiveKey key, string runId, string traceRef, string proposalHash, CancellationToken ct)
    {
        // D-11: the token is obtained before any cache or resolver use. Issuance gets its own timeout budget (WR-01):
        // a hung popeye must fail closed, never hold the request open.
        TokenOutcome token;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(options.Timeout);
            try
            {
                token = await tokens.GetAsync(sessionKey, key, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new TraceVerdict.Unavailable("token_issuer_timeout");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return new TraceVerdict.Unavailable("token_issuer_exception", e.GetType().Name);
            }
        }

        if (token is not TokenOutcome.Ok ok)
        {
            var failed = (TokenOutcome.Failed)token;
            return new TraceVerdict.Unavailable(failed.Cause, failed.ExceptionType);
        }

        var grant = ok.Grant;
        TraceLookup lookup;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(options.Timeout);
            try
            {
                lookup = await resolver.ResolveAsync(grant, traceRef, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new TraceVerdict.Unavailable("trace_resolver_timeout");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // WR-02: a thrown port fault is still fail-closed 503 and retryable, never a 500. Type name only.
                return new TraceVerdict.Unavailable("trace_resolver_exception", e.GetType().Name);
            }
        }

        switch (lookup)
        {
            case TraceLookup.NotFound:
                return new TraceVerdict.NotFound();
            case TraceLookup.CallerRejected rejected:
                alarm.Raise(rejected.Status == 403 ? AlarmCodes.TraceResolver403 : AlarmCodes.TraceResolver401, Component);
                return new TraceVerdict.Unavailable(rejected.Status == 403 ? "trace_resolver_403" : "trace_resolver_401");
            case TraceLookup.Malformed:
                alarm.Raise(AlarmCodes.TraceResponseMalformed, Component);
                return new TraceVerdict.Unavailable("trace_response_malformed");
            case TraceLookup.Found found:
                return Check(found, key, grant, runId, proposalHash);
            default:
                return new TraceVerdict.Unavailable("trace_resolver_unavailable");
        }
    }

    private static TraceVerdict Check(TraceLookup.Found found, ActiveKey key, ServiceTokenGrant grant, string runId, string proposalHash)
    {
        var f = found.Facts;
        string? reason = null;
        if (f.RunId is null || !string.Equals(f.RunId, runId, StringComparison.Ordinal))
        {
            reason = TraceRefusalReasons.Unresolved;
        }
        else if (f.TenantId != key.TenantId)
        {
            reason = TraceRefusalReasons.TenantMismatch;
        }
        else if (f.AgentId is null || !string.Equals(f.AgentId, grant.DelegatedAgentId, StringComparison.Ordinal))
        {
            reason = TraceRefusalReasons.AgentMismatch;
        }
        else if (f.ProposalHash is null || !string.Equals(f.ProposalHash, proposalHash, StringComparison.Ordinal))
        {
            reason = TraceRefusalReasons.ProposalMismatch;
        }

        return reason is null
            ? new TraceVerdict.Verified(found.Source, f.SchemaVersion)
            : new TraceVerdict.Refused(reason, found.Source, f.SchemaVersion);
    }
}
