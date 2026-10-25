namespace Olive.Application;

public sealed record ProducerDefinition(string ClientId, string Producer, IReadOnlyDictionary<string, IReadOnlySet<string>> KindSubtypes);

// D-06: source-controlled and read-only. Maps a verified service client_id to a producer and the kinds and subtypes it may file.
public interface IProducerRegistry
{
    IReadOnlyList<ProducerDefinition> All { get; }

    bool TryGetByClientId(string? clientId, out ProducerDefinition definition);
}
