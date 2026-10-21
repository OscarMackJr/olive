using System.Text.Json;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;
using Olive.Host.Auth;
using Olive.Host.Problems;

namespace Olive.Host.Endpoints;

public static class ProposalEndpoints
{
    public const int MaxBodyBytes = 1_048_576;
    private const int MaxDepth = 64;
    private const int MaxRunIdLength = 256;

    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/proposals", PostAsync).RequireAuthorization();
        app.MapGet("/v1/proposals/{id:guid}", GetAsync).RequireAuthorization();
        return app;
    }

    // Order (Decision section 15.2): introspect (auth handler), principal checks, then the body.
    private static async Task<IResult> PostAsync(HttpContext http, IProposalStore store, IAgentAllowlist allowlist, CancellationToken ct)
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

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            return Invalid();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryRunId(root, out var runId)
                || !TryOptionalString(root, "action_type", out var actionType)
                || !TryOptionalString(root, "party_ref", out var partyRef)
                || !TryOptionalString(root, "justification", out var justification)
                || !TryOptionalString(root, "trace_ref", out var traceRef)
                || !TryOptionalString(root, "nono_session_id", out var nonoSessionId)
                || !TryParameters(root, out var parametersJson))
            {
                return Invalid();
            }

            // Tenant comes only from the introspected key, never from the body (IDN-04).
            var proposal = new NewProposal(key.AgentId, key.KeyId, key.OnBehalfOf, key.Purpose, key.ExpiresAt,
                nonoSessionId, runId, actionType, partyRef, parametersJson, justification, traceRef);
            var id = await store.InsertAsync(key.TenantId, proposal, ct);
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

    private static bool TryRunId(JsonElement root, out string runId)
    {
        runId = "";
        if (!root.TryGetProperty("run_id", out var v) || v.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var s = v.GetString();
        if (string.IsNullOrEmpty(s) || s.Length > MaxRunIdLength || s.Contains('\0'))
        {
            return false;
        }

        runId = s;
        return true;
    }

    private static bool TryOptionalString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var v))
        {
            return true;
        }

        if (v.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = v.GetString();
        return value is not null && !value.Contains('\0');
    }

    private static bool TryParameters(JsonElement root, out string? json)
    {
        json = null;
        if (!root.TryGetProperty("parameters", out var v))
        {
            return true;
        }

        if (v.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var raw = v.GetRawText();
        // jsonb cannot store U+0000; refuse it as a client error rather than a database error.
        if (raw.Contains("\\u0000", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        json = raw;
        return true;
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
