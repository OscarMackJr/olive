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

    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/proposals", PostAsync).RequireAuthorization();
        app.MapGet("/v1/proposals/{id:guid}", GetAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> PostAsync(HttpContext http, IProposalStore store, CancellationToken ct)
    {
        var key = http.User.ToActiveKey();
        using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
        var root = doc.RootElement;
        var proposal = new NewProposal(
            key.AgentId!, key.KeyId, key.OnBehalfOf!, key.Purpose, key.ExpiresAt,
            Str(root, "nono_session_id"), root.GetProperty("run_id").GetString()!,
            Str(root, "action_type"), Str(root, "party_ref"),
            root.TryGetProperty("parameters", out var p) ? p.GetRawText() : null,
            Str(root, "justification"), Str(root, "trace_ref"));
        var id = await store.InsertAsync(key.TenantId, proposal, ct);
        return Results.Created($"/v1/proposals/{id}", new ProposalCreatedResponse(id!.Value, ProposalStates.Proposed));
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) ? v.GetString() : null;

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
