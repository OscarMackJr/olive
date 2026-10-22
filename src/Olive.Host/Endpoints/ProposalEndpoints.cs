using System.Text;
using System.Text.Json;
using Npgsql;
using Olive.Application;
using Olive.Catalog;
using Olive.Contracts;
using Olive.Domain;
using Olive.Host.Auth;
using Olive.Host.Problems;

namespace Olive.Host.Endpoints;

public static class ProposalEndpoints
{
    public const int MaxBodyBytes = 1_048_576;
    private const string LogCategory = "Olive.Host.Endpoints.ProposalEndpoints";

    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/proposals", PostAsync).RequireAuthorization();
        app.MapGet("/v1/proposals/{id:guid}", GetAsync).RequireAuthorization();
        return app;
    }

    // Order (Decision section 15.2): introspect (auth handler), principal checks, then the body.
    private static async Task<IResult> PostAsync(HttpContext http, IProposalStore store, IAgentAllowlist allowlist, SubmissionValidator validator, TraceVerifier verifier, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var key = http.User.ToActiveKey();
        if (key.PrincipalType != PrincipalTypes.Agent)
        {
            return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
        }

        if (key.AgentId is null || key.OnBehalfOf is null || !allowlist.IsAllowed(key.AgentId))
        {
            return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.AgentNotAllowlisted);
        }

        if (!http.Request.HasJsonContentType())
        {
            return Invalid();
        }

        var bytes = await ReadBoundedAsync(http.Request, ct);
        if (bytes is null)
        {
            return ProblemResults.Result(StatusCodes.Status413PayloadTooLarge, ProblemReasons.PayloadTooLarge);
        }

        // Q1: the I-JSON gate (duplicate keys, lossy numbers, surrogates, run_id) runs before any other body step.
        if (!JsonGate.TryAdmit(bytes, out var doc, out var runId))
        {
            return Invalid();
        }

        using (doc)
        {
            var root = doc!.RootElement;

            // Idempotency (D-06/D-07): one canonical form of the whole body, hashed once; replay or conflict is decided before validation.
            var canonical = Jcs.Canonicalize(root);
            var bodySha = Jcs.Sha256Hex(canonical);
            var logger = loggerFactory.CreateLogger(LogCategory);

            async Task<IResult?> DecideExistingAsync()
            {
                var existing = await store.FindByRunAsync(key.TenantId, key.AgentId, runId, ct);
                if (existing is null)
                {
                    return null;
                }

                // The body hash covers only the request body. on_behalf_of and purpose come from the introspected key, so they are
                // compared too: a replay under a different delegation is a conflict, never another caller's proposal id (WR-02).
                var sameDelegation = string.Equals(existing.OnBehalfOf, key.OnBehalfOf, StringComparison.Ordinal)
                    && string.Equals(existing.Purpose, key.Purpose, StringComparison.Ordinal);
                if (existing.BodySha256 is not null && existing.BodySha256 == bodySha && sameDelegation)
                {
                    http.Response.Headers.Location = $"/v1/proposals/{existing.ProposalId}";
                    return Results.Ok(new ProposalCreatedResponse(existing.ProposalId, existing.State, existing.RefusalReason));
                }

                // Ids and truncated hashes only: never the body, parameters, run key or session key.
                logger.LogWarning("Idempotency conflict tenant_id={TenantId} agent_id={AgentId} proposal_id={ProposalId} stored_hash={StoredHash} received_hash={ReceivedHash} delegation_match={DelegationMatch}",
                    key.TenantId, key.AgentId, existing.ProposalId, Short(existing.BodySha256), Short(bodySha), sameDelegation);
                return ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.IdempotencyConflict);
            }

            var replay = await DecideExistingAsync();
            if (replay is not null)
            {
                return replay;
            }

            // Envelope schema, current-release catalog lookup, then catalog parameter schema (D-13). Nothing is stored on failure.
            switch (validator.Validate(root))
            {
                case SubmissionOutcome.Violation v:
                    return ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.SchemaViolation, v.Detail.Pointer, v.Detail.Keyword);
                case SubmissionOutcome.NotInCatalog:
                    return ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.NotInCatalog);
            }

            // The envelope schema has typed every field; the gate has proven the strings clean.
            var actionType = root.GetProperty("action_type").GetString();
            var partyRef = root.GetProperty("party_ref").GetString();
            var justification = root.GetProperty("justification").GetString();
            var traceRef = root.GetProperty("trace_ref").GetString();
            var nonoSessionId = root.TryGetProperty("nono_session_id", out var nono) ? nono.GetString() : null;
            var parametersJson = root.GetProperty("parameters").GetRawText();

            // Trace verification (D-11, D15.2): after idempotency and schema/catalog validation, before anything is stored.
            // The session key goes only to the verifier, which hands it only to the token issuer (D17.1).
            var sessionKey = AgentKeyHeader.TryRead(http.Request.Headers[AgentKeyHeader.Name].ToString());
            if (sessionKey is null)
            {
                return TraceUnavailable();
            }

            var proposalHash = Jcs.ProposalHash(root);
            var verdict = await verifier.VerifyAsync(sessionKey, key, runId, traceRef!, proposalHash, ct);
            string source, schemaVersion;
            string? refusalReason = null;
            switch (verdict)
            {
                case TraceVerdict.Verified verified:
                    source = verified.Source;
                    schemaVersion = verified.SchemaVersion;
                    logger.LogTrace("Trace verified tenant_id={TenantId} agent_id={AgentId} hash={Hash} verdict={Verdict}", key.TenantId, key.AgentId, Short(proposalHash), nameof(TraceVerdict.Verified));
                    break;
                case TraceVerdict.Refused refused:
                    source = refused.Source;
                    schemaVersion = refused.SchemaVersion;
                    refusalReason = refused.Reason;
                    // Static reason codes only: never trace_ref, token or parameters.
                    logger.LogTrace("Trace refused tenant_id={TenantId} agent_id={AgentId} hash={Hash} reason={Reason}", key.TenantId, key.AgentId, Short(proposalHash), refused.Reason);
                    break;
                case TraceVerdict.NotFound:
                    // D-09: never cached, never stored; the caller may retry under the same run_id once the trace exists.
                    return ProblemResults.Retryable(StatusCodes.Status422UnprocessableEntity, ProblemReasons.TraceNotFound);
                default:
                    // Fail closed for every other verdict: nothing is stored. The cause goes only to the Warning log (D-06).
                    if (verdict is TraceVerdict.Unavailable unavailable)
                    {
                        logger.LogWarning("Trace verification unavailable tenant_id={TenantId} agent_id={AgentId} cause={Cause}", key.TenantId, key.AgentId, unavailable.Cause);
                    }

                    return TraceUnavailable();
            }

            // Tenant comes only from the introspected key, never from the body (IDN-04).
            var proposal = new NewProposal(key.AgentId, key.KeyId, key.OnBehalfOf, key.Purpose, key.ExpiresAt,
                nonoSessionId, runId, actionType, partyRef, parametersJson, justification, traceRef,
                Encoding.UTF8.GetString(canonical), bodySha, proposalHash, source, schemaVersion);
            Guid? id;
            try
            {
                id = refusalReason is null
                    ? await store.InsertAsync(key.TenantId, proposal, ct)
                    : await store.InsertRefusedAsync(key.TenantId, proposal, refusalReason, ct);
            }
            catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.NumericValueOutOfRange
                                                 or PostgresErrorCodes.InvalidTextRepresentation
                                                 or PostgresErrorCodes.UntranslatableCharacter
                                                 or PostgresErrorCodes.CharacterNotInRepertoire)
            {
                // Only the data faults a body can cause map to 400 (D-07); other class-22 faults reach the 500 handler.
                // The message may echo values: log the SQL state only, and never return it (WR-11).
                loggerFactory.CreateLogger(LogCategory).LogWarning("Proposal insert refused by database data check {SqlState}", e.SqlState);
                return Invalid();
            }

            if (id is null)
            {
                // Lost the insert race (API-07): the winner's row decides replay or conflict. Missing means fail closed.
                return await DecideExistingAsync() ?? ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.IdempotencyConflict);
            }

            return Results.Created($"/v1/proposals/{id}", refusalReason is null
                ? new ProposalCreatedResponse(id.Value, ProposalStates.Proposed)
                : new ProposalCreatedResponse(id.Value, ProposalStates.RefusedPolicy, refusalReason));
        }
    }

    private static string Short(string? hash) => hash is null ? "none" : hash[..Math.Min(12, hash.Length)];

    private static IResult TraceUnavailable() => ProblemResults.Retryable(StatusCodes.Status503ServiceUnavailable, ProblemReasons.TraceVerificationUnavailable);

    private static IResult Invalid() => ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest);

    // Reads at most MaxBodyBytes; null means the body is larger.
    private static async Task<byte[]?> ReadBoundedAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + n > MaxBodyBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, n);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return ms.ToArray();
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, IProposalStore store, CancellationToken ct)
    {
        var key = http.User.ToActiveKey();
        var r = await store.GetAsync(key.TenantId, id, ct);
        if (r is null)
        {
            return ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ProposalNotFound);
        }

        JsonElement? parameters = null;
        if (r.ParametersJson is not null)
        {
            using var pd = JsonDocument.Parse(r.ParametersJson);
            parameters = pd.RootElement.Clone();
        }

        return Results.Ok(new ProposalResponse(r.ProposalId, r.State, r.TenantId, r.AgentId, r.KeyId, r.OnBehalfOf, r.Purpose,
            r.KeyExpiresAt, r.NonoSessionId, r.RunId, r.ActionType, r.PartyRef, parameters, r.Justification, r.TraceRef, r.CreatedAt, r.RefusalReason, r.TraceVerification, r.TraceSchemaVersion));
    }
}
