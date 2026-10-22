namespace Olive.Application;

public abstract record TraceVerdict
{
    public sealed record Verified(string Source, string SchemaVersion) : TraceVerdict;
    public sealed record Refused(string Reason, string Source, string SchemaVersion) : TraceVerdict;
    public sealed record NotFound : TraceVerdict;
    // Cause is a static code for Warning logs only; the public reason is always trace_verification_unavailable (D-06).
    // ExceptionType is the exception type name only (never its message) when a port threw (WR-02).
    public sealed record Unavailable(string Cause, string? ExceptionType = null) : TraceVerdict;
}

public sealed record TraceVerifierOptions(TimeSpan Timeout)
{
    public TraceVerifierOptions() : this(TimeSpan.FromSeconds(5)) { }
}
