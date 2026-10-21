using System.Text.Json;
using Npgsql;
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
    private const string LogCategory = "Olive.Host.Endpoints.ProposalEndpoints";

    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/proposals", PostAsync).RequireAuthorization();
        app.MapGet("/v1/proposals/{id:guid}", GetAsync).RequireAuthorization();
        return app;
    }

    // Order (Decision section 15.2): introspect (auth handler), principal checks, then the body.
    private static async Task<IResult> PostAsync(HttpContext http, IProposalStore store, IAgentAllowlist allowlist, ILoggerFactory loggerFactory, CancellationToken ct)
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

    private static bool TryRunId(JsonElement root, out string runId)
    {
        runId = "";
        if (!root.TryGetProperty("run_id", out var v) || v.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!TryGetCleanString(v, out var s) || string.IsNullOrEmpty(s) || s.Length > MaxRunIdLength)
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

        return TryGetCleanString(v, out value);
    }

    // GetString throws InvalidOperationException for a lone-surrogate escape; that is a client fault (D-07, CR-01).
    private static bool TryGetCleanString(JsonElement v, out string? value)
    {
        value = null;
        try
        {
            value = v.GetString();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return value is not null && IsWellFormed(value);
    }

    // jsonb rejects U+0000 and unpaired surrogates.
    private static bool IsWellFormed(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetPropertyName(JsonProperty p, out string name)
    {
        name = "";
        try
        {
            name = p.Name;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return IsWellFormed(name);
    }

    // jsonb numeric holds roughly 131072 digits before the point and 16383 after; text expansion on GET is bounded
    // by refusing exponents outside the double range and by a per-request total expansion budget (CR-02, D-07).
    private const int MaxNumberChars = 64;
    private const int MaxExponentDigits = 6;
    private const int MaxExponent = 308;
    private const int MaxNumberExpansionBudget = 65536;

    // Adds the number's worst-case growth over its wire size to budget; false when out of range or over budget.
    private static bool IsNumberInRange(JsonElement e, ref int budget)
    {
        var raw = e.GetRawText();
        if (raw.Length > MaxNumberChars)
        {
            return false;
        }

        var exponent = 0;
        var mantissaEnd = raw.Length;
        var ePos = raw.AsSpan().IndexOfAny('e', 'E');
        if (ePos >= 0)
        {
            mantissaEnd = ePos;
            var expText = raw.AsSpan(ePos + 1);
            var digits = expText.Length > 0 && (expText[0] == '+' || expText[0] == '-') ? expText.Length - 1 : expText.Length;
            if (digits is < 1 or > MaxExponentDigits
                || !int.TryParse(expText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out exponent)
                || exponent is < -MaxExponent or > MaxExponent)
            {
                return false;
            }
        }

        var mantissaDigits = 0;
        for (var i = 0; i < mantissaEnd; i++)
        {
            if (char.IsAsciiDigit(raw[i]))
            {
                mantissaDigits++;
            }
        }

        // Charge only what the stored text can add beyond the raw token, so numbers without an exponent are free (WR-10).
        budget += Math.Max(0, mantissaDigits + Math.Abs(exponent) - raw.Length);
        return budget <= MaxNumberExpansionBudget;
    }

    private static bool AllStringsValid(JsonElement e, ref int budget)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (!TryGetPropertyName(p, out _) || !AllStringsValid(p.Value, ref budget))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    if (!AllStringsValid(item, ref budget))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.String:
                return TryGetCleanString(e, out _);
            case JsonValueKind.Number:
                return IsNumberInRange(e, ref budget);
            default:
                return true;
        }
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

        // jsonb cannot store U+0000 or lone surrogates; refuse them as a client error rather than a database error.
        var budget = 0;
        if (!AllStringsValid(v, ref budget))
        {
            return false;
        }

        json = v.GetRawText();
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
