using Microsoft.AspNetCore.Diagnostics;
using Olive.Application;
using Olive.Contracts;

namespace Olive.Host.Problems;

// Maps unexpected failures to static problems. Logs only the exception type name, never its message.
public sealed class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> logger, IOperationalAlarm alarm) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, reason) = exception is IntrospectionUnavailableException
            ? (StatusCodes.Status503ServiceUnavailable, ProblemReasons.IntrospectionUnavailable)
            : (StatusCodes.Status500InternalServerError, ProblemReasons.InternalError);
        if (exception is IntrospectionCallerRejectedException)
        {
            alarm.Raise(AlarmCodes.IntrospectionCallerRejected, "popeye");
        }

        logger.LogError("Request failed with {ExceptionType}.", exception.GetType().Name);
        await ProblemResults.WriteAsync(httpContext, status, reason);
        return true;
    }
}
