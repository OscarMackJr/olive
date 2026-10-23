using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Catalog;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueListTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Allowed =
    [
        "tenant_id", "item_id", "kind", "subtype", "producer", "producer_ref", "state", "subject_ref", "payload_ref", "claimed_by",
        "created_at", "age_seconds", "expires_at",
    ];

    [Fact]
    public async Task QUE_04_alpha_sees_its_item_beta_does_not_and_default_state_is_open()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var proposal = await ReviewerTenantTests.SubmitAsync(agent, "l1");

        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var item = Assert.Single(await ReviewerTenantTests.ListAsync(alpha));
        Assert.Equal(proposal, item.GetProperty("producer_ref").GetGuid());
        Assert.Equal("open", item.GetProperty("state").GetString());
        Assert.Equal(ApiProposalTests.Alpha, item.GetProperty("tenant_id").GetGuid());

        using var beta = factory.CreateReviewerClient("beta-sup");
        Assert.Empty(await ReviewerTenantTests.ListAsync(beta));
        Assert.Empty(await ReviewerTenantTests.ListAsync(alpha, "?state=claimed"));
        Assert.Single(await ReviewerTenantTests.ListAsync(alpha, "?state=open&kind=action_approval&limit=5"));
        Assert.Empty(await ReviewerTenantTests.ListAsync(alpha, "?kind=identity_review"));
    }

    [Theory]
    [InlineData("?kind=nope")]
    [InlineData("?state=nope")]
    [InlineData("?limit=0")]
    [InlineData("?limit=501")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=-1")]
    public async Task QUE_04_invalid_filters_are_400_invalid_request(string query)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var response = await alpha.GetAsync(Items + query, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("invalid_request", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task QUE_04_items_are_oldest_first()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        for (var i = 0; i < 3; i++) { await ReviewerTenantTests.SubmitAsync(agent, "ord" + i); }

        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var items = await ReviewerTenantTests.ListAsync(alpha);
        Assert.Equal(3, items.Length);
        var created = items.Select(i => i.GetProperty("created_at").GetDateTimeOffset()).ToArray();
        Assert.Equal(created.OrderBy(c => c).ToArray(), created);
    }

    [Fact]
    public async Task QUE_04_expired_open_item_is_not_listed_and_listing_writes_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var n = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, TimeSpan.Zero));
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton<TimeProvider>(clock));
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor("exp1").TrimEnd('}') + ",\"expires_in_seconds\":60}";
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var item = Assert.Single(await ReviewerTenantTests.ListAsync(alpha));
        Assert.True(item.TryGetProperty("expires_at", out _));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(await ReviewerTenantTests.ListAsync(alpha));
        var open = await ApiProposalTests.ReaderScalarAsync<long>(db, ApiProposalTests.Alpha, "select count(*) from queue_item where state = 'open'");
        Assert.Equal(1, open);
        var events = await ApiProposalTests.ReaderScalarAsync<long>(db, ApiProposalTests.Alpha, "select count(*) from queue_item_event");
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task QUE_04_response_items_are_reference_only_and_schema_valid()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "sch1");

        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var schema = SchemaValidator.Compile(File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "queue-item", "v1", "queue-item.schema.json")));
        var item = Assert.Single(await ReviewerTenantTests.ListAsync(alpha));
        foreach (var property in item.EnumerateObject())
        {
            Assert.Contains(property.Name, Allowed);
        }

        Assert.False(item.TryGetProperty("parameters", out _));
        Assert.False(item.TryGetProperty("justification", out _));
        Assert.False(item.TryGetProperty("on_behalf_of", out _));
        Assert.False(item.TryGetProperty("rationale", out _));
        Assert.Null(schema.Validate(item));
    }
}
