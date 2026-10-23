using System.Text.RegularExpressions;
using Olive.Application;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Olive.Catalog;

public sealed partial class AgentAllowlist : IAgentAllowlist
{
    private const string Invalid = "Agent allowlist is invalid.";

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<string, IReadOnlySet<string>> _agents;

    private AgentAllowlist(Dictionary<string, IReadOnlySet<string>> agents) => _agents = agents;

    private sealed class AllowlistDocument { public List<AgentEntry>? Agents { get; set; } }

    private sealed class AgentEntry { public string? AgentId { get; set; } public List<string>? Entitlements { get; set; } }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")]
    private static partial Regex IdPattern();

    public static AgentAllowlist Parse(string yaml)
    {
        AllowlistDocument? doc;
        try
        {
            // Strict mapping: IgnoreUnmatchedProperties is deliberately not set.
            doc = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
                .Deserialize<AllowlistDocument?>(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            throw new InvalidOperationException(Invalid);
        }

        if (doc?.Agents is not { Count: > 0 } agents)
        {
            throw new InvalidOperationException(Invalid);
        }

        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var entry in agents)
        {
            if (entry?.AgentId is not { } id || !IdPattern().IsMatch(id) || map.ContainsKey(id))
            {
                throw new InvalidOperationException(Invalid);
            }

            // D-05: a missing entitlements key means none, which denies. It never grants.
            var ents = entry.Entitlements ?? [];
            if (ents.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(Invalid);
            }

            map[id] = new HashSet<string>(ents, StringComparer.Ordinal);
        }

        return new AgentAllowlist(map);
    }

    public static AgentAllowlist LoadEmbedded()
    {
        using var s = typeof(AgentAllowlist).Assembly.GetManifestResourceStream("config/agents.yaml")
            ?? throw new InvalidOperationException(Invalid);
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public bool IsAllowed(string agentId) => !string.IsNullOrWhiteSpace(agentId) && _agents.ContainsKey(agentId);

    public IReadOnlySet<string> Entitlements(string agentId) =>
        !string.IsNullOrWhiteSpace(agentId) && _agents.TryGetValue(agentId, out var set) ? set : None;
}
