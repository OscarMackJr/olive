using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueuePayloadTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The startup drift guard (QUE-02) compares the catalog's required_approval to the registry role, so the role is swapped only
    // after the host has started.
    private sealed class OtherRoleRegistry(IQueueKindRegistry inner) : IQueueKindRegistry
    {
        public volatile bool Swapped;

        public IReadOnlyList<string> Kinds => inner.Kinds;

        public bool TryGet(string kind, out QueueKindDefinition definition)
        {
            var found = inner.TryGet(kind, out var d);
            definition = found && Swapped ? d with { RequiredRole = "role:other" } : d!;
            return found;
        }
    }

    private static async Task<Guid> SeedAsync(OliveApiFactory factory, string run)
    {
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, run);
        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        return Assert.Single(await ReviewerTenantTests.ListAsync(alpha)).GetProperty("item_id").GetGuid();
    }

    private static async Task<string> BodyAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync(Ct);

    private static async Task<string> ReasonAsync(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await BodyAsync(r));
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    [Fact]
    public async Task D_19_supervisor_reads_exactly_the_payload_members()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var id = await SeedAsync(factory, "pay1");
        using var sup = factory.CreateReviewerClient("alpha-sup-1");
        var response = await sup.GetAsync($"{Items}/{id}/payload", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await BodyAsync(response));
        var root = doc.RootElement;
        Assert.Equal("crm.case_note@1", root.GetProperty("action_type").GetString());
        Assert.Equal("payload://c1", root.GetProperty("parameters").GetProperty("case_ref").GetString());
        Assert.Equal("payload://n1", root.GetProperty("parameters").GetProperty("note_ref").GetString());
        Assert.Equal("j", root.GetProperty("justification").GetString());
        Assert.Equal("trace://t1", root.GetProperty("trace_ref").GetString());
        Assert.True(root.TryGetProperty("on_behalf_of", out _));
        var allowed = new[] { "action_type", "parameters", "justification", "on_behalf_of", "expires_at", "trace_ref" };
        foreach (var p in root.EnumerateObject()) { Assert.Contains(p.Name, allowed); }
    }

    [Fact]
    public async Task D_19_role_less_reviewer_is_403_and_foreign_or_unknown_ids_share_one_404()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var id = await SeedAsync(factory, "pay2");

        using var norole = factory.CreateReviewerClient("alpha-norole");
        var denied = await norole.GetAsync($"{Items}/{id}/payload", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("role_not_permitted", await ReasonAsync(denied));

        using var beta = factory.CreateReviewerClient("beta-sup");
        var foreign = await beta.GetAsync($"{Items}/{id}/payload", Ct);
        var unknown = await beta.GetAsync($"{Items}/{Guid.NewGuid()}/payload", Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("item_not_found", await ReasonAsync(foreign));
        Assert.Equal(await BodyAsync(foreign), await BodyAsync(unknown));
    }

    [Fact]
    public async Task D_05_agent_key_cannot_reach_payload_and_list_stays_reference_only()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var id = await SeedAsync(factory, "pay3");
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var response = await agent.GetAsync($"{Items}/{id}/payload", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", await ReasonAsync(response));

        using var sup = factory.CreateReviewerClient("alpha-sup-1");
        var item = Assert.Single(await ReviewerTenantTests.ListAsync(sup));
        Assert.False(item.TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task Role_gate_reads_the_registry_not_a_hard_coded_role()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var registry = new OtherRoleRegistry(Olive.Catalog.QueueKindRegistry.LoadEmbedded());
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton<IQueueKindRegistry>(registry));
        var id = await SeedAsync(factory, "pay4");
        using var sup = factory.CreateReviewerClient("alpha-sup-1");
        var before = await sup.GetAsync($"{Items}/{id}/payload", Ct);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        registry.Swapped = true;
        var response = await sup.GetAsync($"{Items}/{id}/payload", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("role_not_permitted", await ReasonAsync(response));
    }

    [Fact]
    public async Task Sentinel_problem_bodies_never_echo_parameters_or_justification()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var id = await SeedAsync(factory, "pay5");
        using var norole = factory.CreateReviewerClient("alpha-norole");
        using var beta = factory.CreateReviewerClient("beta-sup");
        foreach (var r in new[] { await norole.GetAsync($"{Items}/{id}/payload", Ct), await beta.GetAsync($"{Items}/{id}/payload", Ct) })
        {
            var body = await BodyAsync(r);
            Assert.DoesNotContain("payload://", body);
            Assert.DoesNotContain("\"j\"", body);
            Assert.DoesNotContain("justification", body);
        }
    }
}
