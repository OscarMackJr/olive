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
}
