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
    private static async Task<IResult> PostAsync(HttpContext http, IProposalStore store, IAgentAllowlist allowlist, SubmissionValidator validator, ILoggerFactory loggerFactory, CancellationToken ct)
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

            // Idempotency slot (plan 07): the run_id lookup and replay/conflict decision go here, before validation.

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

            // Tenant comes only from the introspected key, never from the body (IDN-04).
            var proposal = new NewProposal(key.AgentId, key.KeyId, key.OnBehalfOf, key.Purpose, key.ExpiresAt,
                nonoSessionId, runId, actionType, partyRef, parametersJson, justification, traceRef);
            Guid? id;
            try
            {
                id = await store.InsertAsync(key.TenantId, proposal, ct);
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
                return ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.DuplicateRun);
            }

            return Results.Created($"/v1/proposals/{id}", new ProposalCreatedResponse(id.Value, ProposalStates.Proposed));
        }
    }

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
            r.KeyExpiresAt, r.NonoSessionId, r.RunId, r.ActionType, r.PartyRef, parameters, r.Justification, r.TraceRef, r.CreatedAt));
    }
}
