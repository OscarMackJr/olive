namespace Olive.Application;

// Fail-closed signal. The reason is a static code, never request data.
public sealed class IntrospectionUnavailableException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
