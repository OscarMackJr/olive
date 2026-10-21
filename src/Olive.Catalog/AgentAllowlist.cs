using System.Text.RegularExpressions;
using Olive.Application;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Olive.Catalog;

public sealed partial class AgentAllowlist : IAgentAllowlist
{
    private const string Invalid = "Agent allowlist is invalid.";

    private readonly HashSet<string> _ids;

    private AgentAllowlist(HashSet<string> ids) => _ids = ids;

    private sealed class AllowlistDocument { public List<AgentEntry>? Agents { get; set; } }

    private sealed class AgentEntry { public string? AgentId { get; set; } }

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

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in agents)
        {
            if (entry?.AgentId is not { } id || !IdPattern().IsMatch(id) || !ids.Add(id))
            {
                throw new InvalidOperationException(Invalid);
            }
        }

        return new AgentAllowlist(ids);
    }

    public static AgentAllowlist LoadEmbedded()
    {
        using var s = typeof(AgentAllowlist).Assembly.GetManifestResourceStream("config/agents.yaml")
            ?? throw new InvalidOperationException(Invalid);
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public bool IsAllowed(string agentId) => !string.IsNullOrWhiteSpace(agentId) && _ids.Contains(agentId);
}
