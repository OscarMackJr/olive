namespace Olive.Application;

public sealed record UserIdentityVerifierOptions(TimeSpan Timeout)
{
    public UserIdentityVerifierOptions() : this(TimeSpan.FromSeconds(5)) { }
}

// D-06: fail closed. A timeout or any resolver exception becomes Unavailable; it is never rethrown and never a pass.
// Cause is a static code; ExceptionType is the exception type name, never its message.
public sealed class UserIdentityVerifier(IUserIdentityResolver resolver, UserIdentityVerifierOptions options)
{
    public async Task<(UserIdentityLookup Lookup, string? Cause, string? ExceptionType)> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);
        try
        {
            var lookup = await resolver.ResolveAsync(tenantId, onBehalfOf, cts.Token);
            return (lookup ?? new UserIdentityLookup.Unavailable(), null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new UserIdentityLookup.Unavailable(), "identity_resolver_timeout", null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (new UserIdentityLookup.Unavailable(), "identity_resolver_exception", e.GetType().Name);
        }
    }
}
