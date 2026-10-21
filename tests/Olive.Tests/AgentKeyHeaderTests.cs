using Olive.Application;

namespace Olive.Tests;

public sealed class AgentKeyHeaderTests
{
    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("bearer abc")]
    [InlineData("BEARER abc")]
    [Trait("Category", "Unit")]
    public void IDN_01_agent_key_header_reads_bearer_case_insensitively(string header)
    {
        Assert.Equal("abc", AgentKeyHeader.TryRead(header)!.ExposeSecret());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer  two spaces")]
    [InlineData("Basic abc")]
    [InlineData("Bearer a b")]
    [Trait("Category", "Unit")]
    public void IDN_01_agent_key_header_rejects_malformed(string? header)
    {
        Assert.Null(AgentKeyHeader.TryRead(header));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_agent_key_header_rejects_over_4096_chars()
    {
        Assert.Null(AgentKeyHeader.TryRead("Bearer " + new string('a', 4096)));
        Assert.NotNull(AgentKeyHeader.TryRead("Bearer " + new string('a', 4000)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_agent_key_header_name_is_authorization()
    {
        Assert.Equal("Authorization", AgentKeyHeader.Name);
    }
}
