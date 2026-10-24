using Microsoft.AspNetCore.Authorization;
using Olive.Application;
using Olive.Contracts;
using Olive.Host.Auth;
using Olive.Host.Problems;

namespace Olive.Host.Endpoints;

public static class QueueEndpoints
{
    // D-01/D-03: reviewer list, get, claim and release. There is deliberately no create and no withdraw route in Phase 5.
    public static IEndpointRouteBuilder MapQueueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/queue/items")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DevReviewerAuthenticationHandler.SchemeName })
            .AddEndpointFilter(async (ctx, next) =>
            {
                // D-08: the tenant comes only from the claim set by the server-side group map, never query, header or body.
                if (ctx.HttpContext.User.ToReviewerContext() is null)
                {
                    return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.ReviewerTenantUnresolved);
                }

                return await next(ctx);
            });
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/payload", PayloadAsync);
        group.MapGet("/{id:guid}/events", EventsAsync);
        group.MapPost("/{id:guid}/claim", ClaimAsync);
        group.MapPost("/{id:guid}/release", ReleaseAsync);
        group.MapPost("/{id:guid}/decision", DecisionAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext http, QueueService service, string? kind, string? state, string? limit, CancellationToken ct)
    {
        int? parsed = null;
        if (limit is not null)
        {
            if (!int.TryParse(limit, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var l))
            {
                return ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest);
            }

            parsed = l;
        }

        var reviewer = http.User.ToReviewerContext()!;
        var result = await service.ListAsync(reviewer, kind, state, parsed, ct);
        return result switch
        {
            QueueListResult.Ok ok => Results.Ok(new QueueItemListResponse(ok.Items.Select(ToDto).ToList())),
            _ => ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest),
        };
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var item = await service.GetAsync(http.User.ToReviewerContext()!, id, ct);
        return item is null
            ? ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound)
            : Results.Ok(ToDto(item));
    }

    // Each result switch has an explicit default arm: an unmapped future subtype fails with a static message, not a SwitchExpressionException.
    private static async Task<IResult> PayloadAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.GetPayloadAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            PayloadResult.Found f => Results.Ok(new ReviewerPayloadResponse(f.Payload.ActionType, ParseJson(f.Payload.ParametersJson), f.Payload.Justification, f.Payload.OnBehalfOf, f.Payload.ExpiresAt, f.Payload.TraceRef)),
            PayloadResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            PayloadResult.RoleNotPermitted => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.RoleNotPermitted),
            _ => throw new InvalidOperationException("Unknown payload result."),
        };
    }

    private static async Task<IResult> EventsAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.EventsAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            EventsResult.Found f => Results.Ok(new QueueItemEventsResponse(f.Events.Select(e => new QueueItemEventDto(e.Seq, e.EventType, e.FromState, e.ToState, e.Actor, e.CreatedAt, ParseJson(e.DetailJson))).ToList())),
            EventsResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            _ => throw new InvalidOperationException("Unknown events result."),
        };
    }

    private static async Task<IResult> ClaimAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.ClaimAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            ClaimResult.Claimed c => Results.Ok(ToDto(service.ToView(c.Item))),
            ClaimResult.AlreadyClaimed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.AlreadyClaimed),
            ClaimResult.ProposalExpired => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ProposalExpired),
            ClaimResult.Closed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemClosed),
            ClaimResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            _ => throw new InvalidOperationException("Unknown claim result."),
        };
    }

    private static async Task<IResult> ReleaseAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.ReleaseAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            ReleaseResult.Released r => Results.Ok(ToDto(service.ToView(r.Item))),
            ReleaseResult.NotClaimant => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.NotClaimant),
            ReleaseResult.NotClaimed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemNotClaimed),
            ReleaseResult.ProposalExpired => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ProposalExpired),
            ReleaseResult.Closed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemClosed),
            ReleaseResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            _ => throw new InvalidOperationException("Unknown release result."),
        };
    }

    // APR-01: only decision and rationale are read from the body. Identity, tenant and auth source come from the authenticated claim.
    private static async Task<IResult> DecisionAsync(Guid id, HttpContext http, QueueService service, ExecutionService execution, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var invalid = ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest);
        if (!http.Request.HasJsonContentType()) { return invalid; }
        var bytes = await ProposalEndpoints.ReadBoundedAsync(http.Request, ct);
        if (bytes is null) { return ProblemResults.Result(StatusCodes.Status413PayloadTooLarge, ProblemReasons.PayloadTooLarge); }

        string? decision = null;
        string? rationale = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) { return invalid; }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != System.Text.Json.JsonValueKind.String) { return invalid; }
                switch (p.Name)
                {
                    case "decision": decision = p.Value.GetString(); break;
                    case "rationale": rationale = p.Value.GetString(); break;
                    default: return invalid;
                }
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return invalid;
        }

        var result = await service.DecideAsync(http.User.ToReviewerContext()!, id, decision, rationale, ct);
        await ExecuteAfterApprovalAsync(result, execution, loggerFactory);
        return result switch
        {
            DecisionResult.Decided d => Results.Ok(ToDto(service.ToView(d.Item))),
            DecisionResult.AlreadyDecidedSame d => Results.Ok(ToDto(service.ToView(d.Item))),
            DecisionResult.Conflict => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.DecisionConflict),
            DecisionResult.NotClaimant => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.NotClaimant),
            DecisionResult.NotClaimed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemNotClaimed),
            DecisionResult.Closed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ItemClosed),
            DecisionResult.ProposalExpired => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.ProposalExpired),
            DecisionResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            DecisionResult.RoleNotPermitted => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.RoleNotPermitted),
            DecisionResult.SelfApproval => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.SelfApprovalForbidden),
            DecisionResult.Unresolved => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.ApprovalIdentityUnresolved),
            DecisionResult.Unavailable => ProblemResults.Retryable(StatusCodes.Status503ServiceUnavailable, ProblemReasons.IdentityResolutionUnavailable),
            DecisionResult.KindNotSupported => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.KindNotSupported),
            DecisionResult.InvalidRequest => invalid,
            _ => throw new InvalidOperationException("Unknown decision result."),
        };
    }

    // D-01: execution starts only after the approval transaction has committed, on its own connections. CancellationToken.None keeps
    // a client disconnect from leaving an in-doubt attempt. A fault never changes the decide response (D-04) and is logged statically (D-09).
    private static async Task ExecuteAfterApprovalAsync(DecisionResult result, ExecutionService execution, ILoggerFactory loggerFactory)
    {
        var item = result switch
        {
            DecisionResult.Decided d => d.Item,
            DecisionResult.AlreadyDecidedSame d => d.Item,
            _ => null,
        };
        if (item is null || item.Decision != "approve" || item.ProposalId is not { } proposalId) { return; }

        // WR-03: identifiers are logged as structured fields (they are neither secrets nor bodies); exception text never is.
        var log = loggerFactory.CreateLogger("Olive.Host.Execution");
        try
        {
            var outcome = await execution.ExecuteAsync(item.TenantId, proposalId, CancellationToken.None);
            if (outcome is ExecutionResult.LockTimeout)
            {
                log.LogWarning("Simulated execution lock wait timed out after approval. proposal_id={ProposalId} tenant_id={TenantId}", proposalId, item.TenantId);
            }
        }
        catch (Exception)
        {
            log.LogError("Simulated execution failed after approval. proposal_id={ProposalId} tenant_id={TenantId}", proposalId, item.TenantId);
        }
    }

    private static System.Text.Json.JsonElement? ParseJson(string? json)
    {
        if (json is null) { return null; }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static QueueItemDto ToDto(QueueItemView v) => new(v.TenantId, v.ItemId, v.Kind, v.Subtype, v.Producer, v.ProducerRef, v.State,
        v.SubjectRef, v.PayloadRef, v.CreatedAt, v.AgeSeconds, v.ClaimedBy, v.ExpiresAt, v.Decision, v.DecidedBy, v.DecidedAt, v.AuthSource, v.WithdrawnReason);
}
