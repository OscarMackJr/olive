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

    // The result hierarchies are closed by construction (sealed cases, no other subclasses); CS8509 cannot see that.
#pragma warning disable CS8509
    private static async Task<IResult> PayloadAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.GetPayloadAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            PayloadResult.Found f => Results.Ok(new ReviewerPayloadResponse(f.Payload.ActionType, ParseJson(f.Payload.ParametersJson), f.Payload.Justification, f.Payload.OnBehalfOf, f.Payload.ExpiresAt, f.Payload.TraceRef)),
            PayloadResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
            PayloadResult.RoleNotPermitted => ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.RoleNotPermitted),
        };
    }

    private static async Task<IResult> EventsAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
    {
        var result = await service.EventsAsync(http.User.ToReviewerContext()!, id, ct);
        return result switch
        {
            EventsResult.Found f => Results.Ok(new QueueItemEventsResponse(f.Events.Select(e => new QueueItemEventDto(e.Seq, e.EventType, e.FromState, e.ToState, e.Actor, e.CreatedAt, ParseJson(e.DetailJson))).ToList())),
            EventsResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
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
        };
    }

#pragma warning restore CS8509

    private static System.Text.Json.JsonElement? ParseJson(string? json)
    {
        if (json is null) { return null; }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static QueueItemDto ToDto(QueueItemView v) => new(v.TenantId, v.ItemId, v.Kind, v.Subtype, v.Producer, v.ProducerRef, v.State,
        v.SubjectRef, v.PayloadRef, v.CreatedAt, v.AgeSeconds, v.ClaimedBy, v.ExpiresAt, v.Decision, v.DecidedBy, v.DecidedAt, v.AuthSource, v.WithdrawnReason);
}
