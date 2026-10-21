namespace Olive.Application;

// A wrapper around another component (for example a cache in front of a port) must expose what it wraps, so the
// FND-05 guard can see a non-production component behind it.
public interface IComponentDecorator
{
    object Inner { get; }
}
