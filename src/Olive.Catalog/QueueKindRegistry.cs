using System.Text.RegularExpressions;
using Olive.Application;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Olive.Catalog;

public sealed partial class QueueKindRegistry : IQueueKindRegistry
{
    private const string Invalid = "Queue kind registry is invalid.";

    private readonly Dictionary<string, QueueKindDefinition> _kinds;

    private QueueKindRegistry(Dictionary<string, QueueKindDefinition> kinds) => _kinds = kinds;

    private sealed class RegistryDocument { public List<KindEntry>? Kinds { get; set; } }

    private sealed class KindEntry
    {
        public string? Kind { get; set; }
        public List<string>? Subtypes { get; set; }
        public List<string>? Decisions { get; set; }
        public string? RequiredRole { get; set; }
        public List<string>? RefSchemes { get; set; }
        public bool? Expires { get; set; }
    }

    [GeneratedRegex("^[a-z][a-z_]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^role:[a-z_]+$")]
    private static partial Regex RolePattern();

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex SchemePattern();

    public static QueueKindRegistry Parse(string yaml)
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

        if (doc?.Kinds is not { Count: > 0 } kinds)
        {
            throw new InvalidOperationException(Invalid);
        }

        var map = new Dictionary<string, QueueKindDefinition>(StringComparer.Ordinal);
        foreach (var e in kinds)
        {
            if (e?.Kind is not { } kind || !NamePattern().IsMatch(kind) || map.ContainsKey(kind)
                || e.RequiredRole is not { } role || !RolePattern().IsMatch(role)
                || !AllMatch(e.Subtypes, NamePattern()) || !AllMatch(e.Decisions, NamePattern()) || !AllMatch(e.RefSchemes, SchemePattern()))
            {
                throw new InvalidOperationException(Invalid);
            }

            map[kind] = new QueueKindDefinition(kind, e.Subtypes!.ToArray(), e.Decisions!.ToArray(), role, e.RefSchemes!.ToArray(), e.Expires ?? false);
        }

        return new QueueKindRegistry(map);
    }

    private static bool AllMatch(List<string>? values, Regex pattern) =>
        values is { Count: > 0 } && values.All(v => v is not null && pattern.IsMatch(v)) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    public static QueueKindRegistry LoadEmbedded()
    {
        using var s = typeof(QueueKindRegistry).Assembly.GetManifestResourceStream("config/queue-kinds.yaml")
            ?? throw new InvalidOperationException(Invalid);
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public IReadOnlyList<string> Kinds => _kinds.Keys.ToArray();

    public bool TryGet(string kind, out QueueKindDefinition definition)
    {
        if (kind is not null && _kinds.TryGetValue(kind, out var d))
        {
            definition = d;
            return true;
        }

        definition = null!;
        return false;
    }
}
