using Olive.Catalog;

namespace Olive.Tests;

public sealed class AgentAllowlistTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_02_embedded_allowlist_contains_exactly_liam()
    {
        var a = AgentAllowlist.LoadEmbedded();
        Assert.True(a.IsAllowed("liam"));
    }

    [Theory]
    [InlineData("mallory")]
    [InlineData("Liam")]
    [InlineData("")]
    [InlineData("  ")]
    [Trait("Category", "Unit")]
    public void IDN_02_other_ids_are_not_allowed(string id)
    {
        Assert.False(AgentAllowlist.LoadEmbedded().IsAllowed(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("agents: []")]
    [InlineData("agents:\n  - {}")]
    [InlineData("agents:\n  - agent_id: liam\n  - agent_id: liam")]
    [InlineData("agents:\n  - agent_id: Liam")]
    [InlineData("agents:\n  - agent_id: 1liam")]
    [InlineData("agents:\n  - agent_id: liam\n    extra: x")]
    [InlineData("agents:\n  - agent_id: liam\nother: 1")]
    [InlineData("agents: [unclosed")]
    [InlineData("agents:\n  - agent_id: liam\n    entitlements: [\"\"]")]
    [InlineData("agents:\n  - agent_id: liam\n    entitlements: not-a-list")]
    [Trait("Category", "Unit")]
    public void IDN_02_invalid_documents_are_rejected(string yaml)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AgentAllowlist.Parse(yaml));
        Assert.Equal("Agent allowlist is invalid.", ex.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_02_valid_document_with_two_agents_parses()
    {
        var a = AgentAllowlist.Parse("agents:\n  - agent_id: liam\n  - agent_id: noor-2\n");
        Assert.True(a.IsAllowed("noor-2"));
        Assert.True(a.IsAllowed("liam"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void POL_07_agent_entitlements_parse_and_embedded_liam_holds_crm_write()
    {
        var a = AgentAllowlist.Parse("agents:\n  - agent_id: liam\n    entitlements: [ent:crm.case_note.write]\n");
        Assert.True(a.IsAllowed("liam"));
        Assert.Contains("ent:crm.case_note.write", a.Entitlements("liam"));
        Assert.Contains("ent:crm.case_note.write", AgentAllowlist.LoadEmbedded().Entitlements("liam"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void POL_07_missing_key_or_unknown_agent_yields_empty_entitlements()
    {
        var a = AgentAllowlist.Parse("agents:\n  - agent_id: liam\n");
        Assert.Empty(a.Entitlements("liam"));
        Assert.Empty(a.Entitlements("mallory"));
        Assert.Empty(a.Entitlements(""));
    }
}
