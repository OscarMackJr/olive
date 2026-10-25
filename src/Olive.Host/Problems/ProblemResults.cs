using Microsoft.AspNetCore.Mvc;
using Olive.Contracts;

namespace Olive.Host.Problems;

public static class ProblemResults
{
    public static ProblemDetails Create(int status, string reason)
    {
        var problem = new ProblemDetails
        {
            Type = $"urn:olive:problem:{reason}",
            Title = Title(reason),
            Status = status,
        };
        problem.Extensions["reason"] = reason;
        return problem;
    }

    // Static text only; never request data or exception text.
    private static string Title(string reason) => reason switch
    {
        ProblemReasons.KeyInactive => "The session key is not active.",
        ProblemReasons.PrincipalNotPermitted => "This principal type may not perform this action.",
        ProblemReasons.AgentNotAllowlisted => "This agent is not allowlisted.",
        ProblemReasons.InvalidRequest => "The request is invalid.",
        ProblemReasons.ProposalNotFound => "The proposal was not found.",
        ProblemReasons.IdempotencyConflict => "A different request already exists for this run.",
        ProblemReasons.CatalogVersionNotFound => "The catalog version was not found.",
        ProblemReasons.SchemaViolation => "The request does not satisfy the contract schema.",
        ProblemReasons.NotInCatalog => "The action type is not in the catalog.",
        ProblemReasons.PayloadTooLarge => "The request body is too large.",
        ProblemReasons.IntrospectionUnavailable => "Key introspection is unavailable.",
        ProblemReasons.TraceNotFound => "The trace was not found.",
        ProblemReasons.TraceVerificationUnavailable => "Trace verification is unavailable.",
        ProblemReasons.DirectoryUnavailable => "Directory lookup is unavailable.",
        ProblemReasons.ReviewerUnauthenticated => "The reviewer is not authenticated.",
        ProblemReasons.ReviewerTenantUnresolved => "The reviewer tenant could not be resolved.",
        ProblemReasons.RoleNotPermitted => "The reviewer role does not permit this action.",
        ProblemReasons.ItemNotFound => "The queue item was not found.",
        ProblemReasons.AlreadyClaimed => "The queue item is already claimed.",
        ProblemReasons.NotClaimant => "Only the claimant may release this queue item.",
        ProblemReasons.ItemNotClaimed => "The queue item is not claimed.",
        ProblemReasons.ItemClosed => "The queue item is closed.",
        ProblemReasons.ProposalExpired => "The proposal has expired.",
        ProblemReasons.DecisionConflict => "The queue item was already decided differently.",
        ProblemReasons.SelfApprovalForbidden => "A requester may not approve their own proposal.",
        ProblemReasons.ApprovalIdentityUnresolved => "The requester identity could not be resolved.",
        ProblemReasons.IdentityResolutionUnavailable => "Identity resolution is unavailable.",
        ProblemReasons.KindNotSupported => "This queue item kind does not support decisions yet.",
        ProblemReasons.RoleNotHeld => "The reviewer no longer holds the required role.",
        ProblemReasons.GroupsOverage => "The sign-in token did not include the reviewer groups.",
        ProblemReasons.NoReviewerTenant => "The reviewer account is not mapped to exactly one tenant.",
        ProblemReasons.TenantNotPermitted => "This tenant is not permitted for this caller.",
        ProblemReasons.ProducerNotRegistered => "This producer is not registered.",
        ProblemReasons.ServerAssignedField => "The request sets a field that only the server assigns.",
        ProblemReasons.ItemWithdrawn => "The queue item was withdrawn by its producer.",
        ProblemReasons.NoPayload => "This queue item has no payload to read.",
        _ => "An internal error occurred.",
    };

    public static async Task WriteAsync(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(Create(status, reason), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", context.RequestAborted);
    }

    // Pointer and keyword are schema locations, never caller values.
    public static IResult Result(int status, string reason, string pointer, string keyword)
    {
        var problem = Create(status, reason);
        problem.Extensions["pointer"] = pointer;
        problem.Extensions["keyword"] = keyword;
        return Results.Json(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", status);
    }

    public static IResult Result(int status, string reason) =>
        Results.Json(Create(status, reason), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", status);

    // D-06, D-09: the caller may retry. Retry-After (seconds) is set only for 503.
    public static IResult Retryable(int status, string reason)
    {
        var problem = Create(status, reason);
        problem.Extensions["retryable"] = true;
        var result = Results.Json(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", status);
        return status == StatusCodes.Status503ServiceUnavailable ? new RetryAfterResult(result, 5) : result;
    }

    private sealed class RetryAfterResult(IResult inner, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["Retry-After"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
