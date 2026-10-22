namespace Olive.Application;

// D-13: popeye rejected Olive itself as a caller (401 or 403). Distinct from an inactive session key, which is a normal 401.
public sealed class IntrospectionCallerRejectedException(int status) : IntrospectionUnavailableException("introspection_caller_rejected")
{
    public int Status { get; } = status;
}
