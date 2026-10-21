namespace Olive.Host.Startup;

// Exposes the registration list to the startup guards (FND-05 descriptor scan).
public sealed class ServiceCollectionAccessor(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;
}
