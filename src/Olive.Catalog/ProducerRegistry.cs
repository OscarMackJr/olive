using System.Text.RegularExpressions;
using Olive.Application;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Olive.Catalog;

public sealed partial class ProducerRegistry : IProducerRegistry
{
    private const string Invalid = "Producer registry is invalid.";

    private readonly Dictionary<string, ProducerDefinition> _byClient;

    private ProducerRegistry(Dictionary<string, ProducerDefinition> byClient) => _byClient = byClient;

    private sealed class RegistryDocument { public List<ProducerEntry>? Producers { get; set; } }

    private sealed class ProducerEntry { public string? ClientId { get; set; } public string? Producer { get; set; } public List<KindEntry>? Kinds { get; set; } }

    private sealed class KindEntry { public string? Kind { get; set; } public List<string>? Subtypes { get; set; } }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")]
    private static partial Regex ClientIdPattern();

    [GeneratedRegex("^[a-z][a-z_]*$")]
    private static partial Regex NamePattern();

    public static ProducerRegistry Parse(string yaml)
    {
        RegistryDocument? doc;
        try
        {
            // Strict mapping: IgnoreUnmatchedProperties is deliberately not set.
            doc = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
                .Deserialize<RegistryDocument?>(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            throw new InvalidOperationException(Invalid);
        }

        if (doc?.Producers is not { Count: > 0 } producers)
        {
            throw new InvalidOperationException(Invalid);
        }

        var map = new Dictionary<string, ProducerDefinition>(StringComparer.Ordinal);
        foreach (var e in producers)
        {
            if (e?.ClientId is not { } clientId || !ClientIdPattern().IsMatch(clientId) || map.ContainsKey(clientId)
                || e.Producer is not { } producer || !NamePattern().IsMatch(producer)
                || e.Kinds is not { Count: > 0 } kinds)
            {
                throw new InvalidOperationException(Invalid);
            }

            var kindSubtypes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            foreach (var k in kinds)
            {
                if (k?.Kind is not { } kind || !NamePattern().IsMatch(kind) || kindSubtypes.ContainsKey(kind)
                    || k.Subtypes is not { Count: > 0 } subtypes || subtypes.Any(s => s is null || !NamePattern().IsMatch(s)))
                {
                    throw new InvalidOperationException(Invalid);
                }

                kindSubtypes[kind] = new HashSet<string>(subtypes, StringComparer.Ordinal);
            }

            map[clientId] = new ProducerDefinition(clientId, producer, kindSubtypes);
        }

        return new ProducerRegistry(map);
    }

    public static ProducerRegistry LoadEmbedded()
    {
        using var s = typeof(ProducerRegistry).Assembly.GetManifestResourceStream("config/producers.yaml")
            ?? throw new InvalidOperationException(Invalid);
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public IReadOnlyList<ProducerDefinition> All => _byClient.Values.ToArray();

    public bool TryGetByClientId(string? clientId, out ProducerDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && _byClient.TryGetValue(clientId, out var d))
        {
            definition = d;
            return true;
        }

        definition = null!;
        return false;
    }
}
