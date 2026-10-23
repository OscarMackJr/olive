namespace Olive.Application;

public abstract record DirectoryVerdict
{
    public sealed record Groups(IReadOnlyList<string> Names) : DirectoryVerdict;
    public sealed record UnknownUser : DirectoryVerdict;
    // Cause is a static code for Warning logs only; ExceptionType is the exception type name, never its message.
    public sealed record Unavailable(string Cause, string? ExceptionType = null) : DirectoryVerdict;
}

// WR-03: the directory has its own timeout, independent of the trace verifier's, with the same default.
public sealed record DirectoryVerifierOptions(TimeSpan Timeout)
{
    public DirectoryVerifierOptions() : this(TimeSpan.FromSeconds(5)) { }
}

// D-05: fail closed. Every non-answer is Unavailable; credential rejection and malformed responses raise an alarm. No caching (D-07).
public sealed class DirectoryVerifier(IDirectoryGroupResolver resolver, IOperationalAlarm alarm, DirectoryVerifierOptions options)
{
    private const string Component = "directory";

    public async Task<DirectoryVerdict> VerifyAsync(Guid tenantId, string onBehalfOf, CancellationToken ct)
    {
        DirectoryLookup lookup;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(options.Timeout);
            try
            {
                lookup = await resolver.ResolveAsync(tenantId, onBehalfOf, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new DirectoryVerdict.Unavailable("directory_resolver_timeout");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return new DirectoryVerdict.Unavailable("directory_resolver_exception", e.GetType().Name);
            }
        }

        switch (lookup)
        {
            case DirectoryLookup.Found found:
                return new DirectoryVerdict.Groups(found.Groups ?? []);
            case DirectoryLookup.UnknownUser:
                return new DirectoryVerdict.UnknownUser();
            case DirectoryLookup.CallerRejected rejected:
                alarm.Raise(rejected.Status == 403 ? AlarmCodes.DirectoryResolver403 : AlarmCodes.DirectoryResolver401, Component);
                return new DirectoryVerdict.Unavailable(rejected.Status == 403 ? "directory_resolver_403" : "directory_resolver_401");
            case DirectoryLookup.Malformed:
                alarm.Raise(AlarmCodes.DirectoryResponseMalformed, Component);
                return new DirectoryVerdict.Unavailable("directory_response_malformed");
            default:
                return new DirectoryVerdict.Unavailable("directory_resolver_unavailable");
        }
    }
}
