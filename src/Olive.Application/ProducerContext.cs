namespace Olive.Application;

// D-06/D-08: the producer identity for a queue request, derived only from the verified service token plus producers.yaml.
public sealed record ProducerContext(Guid TenantId, string Producer, string ClientId, IReadOnlyDictionary<string, IReadOnlySet<string>> KindSubtypes);
