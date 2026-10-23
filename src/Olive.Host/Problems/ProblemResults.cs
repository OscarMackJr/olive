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
