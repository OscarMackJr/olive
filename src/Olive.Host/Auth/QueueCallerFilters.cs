using Olive.Application;
using Olive.Contracts;
using Olive.Host.Problems;

namespace Olive.Host.Auth;

// D-07: caller-type gates for the shared queue routes. Decisions key on the authentication type and verified principal_type,
// never on claim presence (the service and reviewer principals share the olive:tenant_id claim name). Refusals are static and echo nothing.
public static class QueueCallerFilters
{
    public const string ProducerItemKey = "olive:producer_context";

    // Null means the caller is a reviewer with a resolved tenant.
    public static IResult? ReviewerCheck(HttpContext http)
    {
        if (http.User.Identity?.AuthenticationType != DevReviewerAuthenticationHandler.SchemeName)
        {
            return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
        }

        // D-08 (Phase 5): the tenant comes only from the claim set by the server-side group map, never query, header or body.
        return http.User.ToReviewerContext() is null
            ? ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.ReviewerTenantUnresolved)
            : null;
    }

    public static IResult? ProducerCheck(HttpContext http, IProducerRegistry registry, IReviewerTenantMap tenants, out ProducerContext? context)
    {
        context = null;
        if (http.User.Identity?.AuthenticationType != ServiceTokenAuthenticationHandler.SchemeName
            || http.User.ToServiceCaller() is not { } caller
            || caller.PrincipalType != "application"
            || caller.DelegatedAgentId is not null)
        {
            return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
        }

        if (!registry.TryGetByClientId(caller.ClientId, out var definition))
        {
            return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.ProducerNotRegistered);
        }

        if (!tenants.AllTenants.Contains(caller.TenantId))
        {
            return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.TenantNotPermitted);
        }

        context = new ProducerContext(caller.TenantId, definition.Producer, definition.ClientId, definition.KindSubtypes);
        return null;
    }

    public static ValueTask<object?> ReviewerOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ReviewerCheck(ctx.HttpContext) is { } refusal ? ValueTask.FromResult<object?>(refusal) : next(ctx);

    public static ValueTask<object?> ProducerOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var services = ctx.HttpContext.RequestServices;
        var refusal = ProducerCheck(ctx.HttpContext, services.GetRequiredService<IProducerRegistry>(), services.GetRequiredService<IReviewerTenantMap>(), out var producer);
        if (refusal is not null) { return ValueTask.FromResult<object?>(refusal); }
        ctx.HttpContext.Items[ProducerItemKey] = producer;
        return next(ctx);
    }
}
