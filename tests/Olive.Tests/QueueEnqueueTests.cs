using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Catalog;
using Olive.Domain;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueEnqueueTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Alpha = ApiProposalTests.Alpha;

    private static Task<T?> Scalar<T>(TestDatabase db, string sql, params (string, object)[] args) =>
        ApiProposalTests.ReaderScalarAsync<T>(db, Alpha, sql, args);

    private static Task<HttpResponseMessage> Post(HttpClient c, string run) =>
        c.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.BodyFor(run)), Ct);

    private static async Task<Guid> IdOf(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();

    [Fact]
    public async Task QUE_03_accepted_submission_creates_one_open_action_approval_item_and_created_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var post = await Post(client, "q1");
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var id = await IdOf(post);
        var refx = "olive:proposal/" + id.ToString("D");

        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item"));
        Assert.Equal(1, await Scalar<long>(db,
            "select count(*) from queue_item q join proposal p on p.tenant_id = q.tenant_id and p.proposal_id = q.proposal_id " +
            "where q.kind = 'action_approval' and q.subtype = 'proposal' and q.state = 'open' and q.producer = 'olive' " +
            "and q.producer_ref = @p and q.subject_ref = @r and q.payload_ref = @r and q.expires_at = p.expires_at and p.expires_at is not null",
            ("p", id), ("r", refx)));
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item_event"));
        Assert.Equal(1, await Scalar<long>(db,
            "select count(*) from queue_item_event where event_type = 'created' and from_state is null and to_state = 'open' and actor = 'liam'"));
    }

    [Fact]
    public async Task QUE_03_idempotent_replay_leaves_exactly_one_item()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        Assert.Equal(HttpStatusCode.Created, (await Post(client, "rep")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "rep")).StatusCode);

        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item"));
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item_event"));
    }

    [Fact]
    public async Task QUE_03_trace_refused_submission_creates_no_item()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        var body = TraceFixtureLoader.Seed(factory, "run-differs");
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from proposal where state = 'refused_policy'"));
        Assert.Equal(0, await Scalar<long>(db, "select count(*) from queue_item"));
        Assert.Equal(0, await Scalar<long>(db, "select count(*) from queue_item_event"));
    }

    [Fact]
    public async Task QUE_03_purpose_refused_submission_creates_no_item()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var any = SchemaValidator.Compile("{\"type\":\"object\"}");
        var catalog = new CatalogSet([new CatalogRelease(1, [new CatalogEntry("crm.case_note@1", JsonDocument.Parse("{}").RootElement, any, RequiredPurpose: ["collections"])])]);
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(catalog));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        Assert.Equal(HttpStatusCode.Created, (await Post(client, "p1")).StatusCode);
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from proposal where refusal_reason = 'purpose_not_permitted'"));
        Assert.Equal(0, await Scalar<long>(db, "select count(*) from queue_item"));
    }

    [Fact]
    public async Task QUE_03_rate_refused_submission_creates_no_item()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(1, 3600, "[servicing, collections]")));
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        Assert.Equal(HttpStatusCode.Created, (await Post(client, "r1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, "r2")).StatusCode);
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from proposal where refusal_reason = 'rate_limited'"));
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item"));
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item_event"));
    }

    [Fact]
    public async Task QUE_03_item_insert_fault_rolls_back_the_proposal()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        await db.AdminExecAsync(
            "create function queue_item_fault() returns trigger language plpgsql as $$ begin raise exception 'injected queue_item fault'; end $$; " +
            "create trigger queue_item_fault before insert on queue_item for each row execute function queue_item_fault();");
        try
        {
            var failed = await Post(client, "fault1");
            Assert.NotEqual(HttpStatusCode.Created, failed.StatusCode);
            Assert.Equal(0, await Scalar<long>(db, "select count(*) from proposal"));
            Assert.Equal(0, await Scalar<long>(db, "select count(*) from queue_item"));
        }
        finally
        {
            await db.AdminExecAsync("drop trigger if exists queue_item_fault on queue_item; drop function if exists queue_item_fault();");
        }

        var ok = await Post(client, "fault2");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from proposal"));
        Assert.Equal(1, await Scalar<long>(db, "select count(*) from queue_item where state = 'open'"));
    }
}
