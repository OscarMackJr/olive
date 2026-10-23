using Microsoft.AspNetCore.Authorization;
using Olive.Application;
using Olive.Contracts;
using Olive.Host.Auth;
using Olive.Host.Problems;

namespace Olive.Host.Endpoints;

public static class QueueEndpoints
{
    // D-01/D-03: reviewer list and get only. There is deliberately no create and no withdraw route in Phase 5.
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

    private static QueueItemDto ToDto(QueueItemView v) => new(v.TenantId, v.ItemId, v.Kind, v.Subtype, v.Producer, v.ProducerRef, v.State,
        v.SubjectRef, v.PayloadRef, v.CreatedAt, v.AgeSeconds, v.ClaimedBy, v.ExpiresAt, v.Decision, v.DecidedBy, v.DecidedAt, v.AuthSource, v.WithdrawnReason);
}
