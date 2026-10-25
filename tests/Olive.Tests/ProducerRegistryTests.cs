using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
using Olive.Catalog;
using Olive.Host.Startup;

namespace Olive.Tests;

public sealed class ProducerRegistryTests
{
    private const string Shipped = "producers:\n  - client_id: bluto-stub\n    producer: bluto\n    kinds:\n      - kind: identity_review\n        subtypes: [candidate_pair]\n";

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_04_embedded_registry_maps_bluto_stub_to_bluto_candidate_pair()
    {
        var r = ProducerRegistry.LoadEmbedded();
        Assert.True(r.TryGetByClientId("bluto-stub", out var d));
        Assert.Equal("bluto", d.Producer);
        Assert.Equal(["identity_review"], d.KindSubtypes.Keys.ToArray());
        Assert.Equal(["candidate_pair"], d.KindSubtypes["identity_review"].ToArray());
        Assert.Single(r.All);
    }

    [Theory]
    [InlineData("")]
    [InlineData("producers: []")]
    [InlineData("producers:\n  - {}")]
    [InlineData("producers:\n  - client_id: bluto-stub\n    producer: bluto\n    kinds:\n      - kind: identity_review\n        subtypes: [candidate_pair]\n    extra: x")]
    [InlineData("producers:\n  - client_id: a\n    producer: bluto\n    kinds:\n      - kind: identity_review\n        subtypes: [candidate_pair]\n  - client_id: a\n    producer: bluto\n    kinds:\n      - kind: identity_review\n        subtypes: [candidate_pair]")]
    [InlineData("producers:\n  - client_id: Bad_Id\n    producer: bluto\n    kinds:\n      - kind: identity_review\n        subtypes: [candidate_pair]")]
    [InlineData("producers:\n  - client_id: a\n    producer: bluto\n    kinds: []")]
    [InlineData("producers:\n  - client_id: a\n    producer: bluto\n    kinds:\n      - kind: identity_review\n        subtypes: []")]
    [InlineData("producers: [unclosed")]
    [Trait("Category", "Unit")]
    public void IRQ_04_invalid_documents_are_rejected_with_a_static_message(string yaml)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProducerRegistry.Parse(yaml));
        Assert.Equal("Producer registry is invalid.", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("unknown-client")]
    [InlineData("Bluto-Stub")]
    [Trait("Category", "Unit")]
    public void IRQ_04_unknown_or_blank_client_ids_are_not_found(string? id) =>
        Assert.False(ProducerRegistry.LoadEmbedded().TryGetByClientId(id, out _));

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_04_environment_variable_does_not_change_the_loaded_registry()
    {
        var before = Environment.GetEnvironmentVariable("OLIVE_PRODUCERS");
        Environment.SetEnvironmentVariable("OLIVE_PRODUCERS", Shipped.Replace("bluto-stub", "evil-client"));
        try
        {
            var r = ProducerRegistry.LoadEmbedded();
            Assert.True(r.TryGetByClientId("bluto-stub", out _));
            Assert.False(r.TryGetByClientId("evil-client", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OLIVE_PRODUCERS", before);
        }
    }

    private static IServiceProvider Provider(string producersYaml, string? kindsYaml = null)
    {
        var s = new ServiceCollection();
        s.AddSingleton<IProducerRegistry>(ProducerRegistry.Parse(producersYaml));
        s.AddSingleton<IQueueKindRegistry>(kindsYaml is null ? QueueKindRegistry.LoadEmbedded() : QueueKindRegistry.Parse(kindsYaml));
        return s.BuildServiceProvider();
    }

    private static string Producer(string producer, string kind, string subtype) =>
        $"producers:\n  - client_id: bluto-stub\n    producer: {producer}\n    kinds:\n      - kind: {kind}\n        subtypes: [{subtype}]\n";

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_shipped_producers_pass_the_guard() => StartupGuards.CheckProducers(Provider(Shipped));

    [Theory]
    [InlineData("bluto", "no_such_kind", "candidate_pair")]
    [InlineData("bluto", "identity_review", "link_challenge")]
    [InlineData("olive", "identity_review", "candidate_pair")]
    [InlineData("other", "identity_review", "candidate_pair")]
    [Trait("Category", "Unit")]
    public void QUE_02_unregistered_kind_subtype_or_foreign_ref_scheme_refuses_with_static_message(string producer, string kind, string subtype)
    {
        var e = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckProducers(Provider(Producer(producer, kind, subtype))));
        Assert.Equal("QUE-02", e.Rule);
        Assert.DoesNotContain("link_challenge", e.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_identity_review_producer_other_than_bluto_refuses()
    {
        // A kind registry that lists a second ref scheme would otherwise let a non-bluto producer through.
        const string kinds = "kinds:\n  - kind: identity_review\n    subtypes: [candidate_pair]\n    decisions: [accept]\n    required_role: role:identity_steward\n    ref_schemes: [bluto, other]\n";
        StartupGuards.CheckProducers(Provider(Producer("bluto", "identity_review", "candidate_pair"), kinds));
        var e = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckProducers(Provider(Producer("other", "identity_review", "candidate_pair"), kinds)));
        Assert.Equal("QUE-02", e.Rule);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_unresolvable_registry_refuses() =>
        Assert.Equal("QUE-02", Assert.Throws<StartupGuardException>(() => StartupGuards.CheckProducers(new ServiceCollection().BuildServiceProvider())).Rule);
}
