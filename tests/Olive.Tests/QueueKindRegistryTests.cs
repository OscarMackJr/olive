using System.Reflection;
using Olive.Application;
using Olive.Catalog;

namespace Olive.Tests;

public sealed class QueueKindRegistryTests
{
    private const string Good = "kinds:\n  - kind: k\n    subtypes: [s]\n    decisions: [a]\n    required_role: role:r\n    ref_schemes: [olive]\n    expires: true\n";

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_embedded_registry_defines_action_approval()
    {
        var r = QueueKindRegistry.LoadEmbedded();
        Assert.True(r.TryGet("action_approval", out var d));
        Assert.Equal(["approve", "reject"], d.Decisions);
        Assert.Equal("role:case_supervisor", d.RequiredRole);
        Assert.Equal(["olive"], d.RefSchemes);
        Assert.True(d.Expires);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_embedded_registry_defines_identity_review_without_link_challenge()
    {
        var r = QueueKindRegistry.LoadEmbedded();
        Assert.True(r.TryGet("identity_review", out var d));
        Assert.Equal(["accept", "reject", "defer"], d.Decisions);
        Assert.Equal("role:identity_steward", d.RequiredRole);
        Assert.Equal(["bluto"], d.RefSchemes);
        Assert.Contains("candidate_pair", d.Subtypes);
        Assert.DoesNotContain("link_challenge", d.Subtypes);
        Assert.False(r.TryGet("unknown", out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_valid_document_parses() => Assert.True(QueueKindRegistry.Parse(Good).TryGet("k", out _));

    [Theory]
    [InlineData("")]
    [InlineData("kinds: []")]
    [InlineData("bogus: 1")]
    [InlineData("kinds:\n  - kind: k\n    extra: 1\n    subtypes: [s]\n    decisions: [a]\n    required_role: role:r\n    ref_schemes: [olive]\n")]
    [InlineData("kinds:\n  - kind: k\n    subtypes: [s]\n    decisions: [a]\n    ref_schemes: [olive]\n")]
    [InlineData("kinds:\n  - kind: k\n    subtypes: [s]\n    decisions: []\n    required_role: role:r\n    ref_schemes: [olive]\n")]
    [InlineData("kinds:\n  - kind: k\n    subtypes: [s]\n    decisions: [a]\n    required_role: Role:R\n    ref_schemes: [olive]\n")]
    [InlineData("kinds:\n  - kind: k\n    subtypes: [s]\n    decisions: [a]\n    required_role: caseworker\n    ref_schemes: [olive]\n")]
    [InlineData("kinds:\n  - kind: k\n    subtypes: [s]\n    decisions: [a]\n    required_role: role:r\n    ref_schemes: [Olive]\n")]
    [InlineData("kinds:\n  - kind: K1\n    subtypes: [s]\n    decisions: [a]\n    required_role: role:r\n    ref_schemes: [olive]\n")]
    [Trait("Category", "Unit")]
    public void QUE_02_invalid_documents_are_refused_with_static_message(string yaml)
    {
        var e = Assert.Throws<InvalidOperationException>(() => QueueKindRegistry.Parse(yaml));
        Assert.Equal("Queue kind registry is invalid.", e.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_duplicate_kind_is_refused()
    {
        var dup = Good + Good["kinds:\n".Length..];
        Assert.Throws<InvalidOperationException>(() => QueueKindRegistry.Parse(dup));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_registry_has_no_runtime_mutator()
    {
        foreach (var t in new[] { typeof(IQueueKindRegistry), typeof(QueueKindRegistry) })
        {
            var names = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.Name).Where(n => n is not "Parse" and not "LoadEmbedded");
            Assert.DoesNotContain(names, n => n.StartsWith("Register") || n.StartsWith("Add") || n.StartsWith("Set") || n.StartsWith("set_"));
        }
    }
}
