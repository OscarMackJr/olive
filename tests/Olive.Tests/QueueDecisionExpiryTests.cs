using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueDecisionExpiryTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (OliveApiFactory Factory, ManualTimeProvider Clock) Build(TestDatabase db)
    {
        var n = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, TimeSpan.Zero));
        return (new OliveApiFactory(db, configureServices: s => s.AddSingleton<TimeProvider>(clock)), clock);
    }

    [Fact]
    public async Task POL_08_approve_after_expiry_commits_the_expiry_then_409_and_never_calls_the_resolver()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (factory, clock) = Build(db);
        await using var _ = factory;
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "e1", expiresIn: 60);
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        clock.Advance(TimeSpan.FromSeconds(60));
        var response = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("proposal_expired", await DecisionHttp.ReasonOf(response));
        Assert.Equal(0, factory.UserIdentity.CallCount);
        Assert.Equal("expired", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal("withdrawn", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("proposal_expired", await DecisionHttp.Scalar<string>(db, "select withdrawn_reason from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(1L, await DecisionHttp.EventCount(db, item, "claimed"));
        Assert.Equal(1L, await DecisionHttp.EventCount(db, item, "withdrawn"));

        var repeat = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal("proposal_expired", await DecisionHttp.ReasonOf(repeat));
        Assert.Equal(1L, await DecisionHttp.EventCount(db, item, "withdrawn"));
        Assert.Equal(0, factory.UserIdentity.CallCount);
    }

    [Fact]
    public async Task D_12_non_claimant_and_decided_cases_do_not_call_the_resolver()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (factory, _) = Build(db);
        await using var _f = factory;
        var (item, _p) = await DecisionHttp.SubmitAsync(factory, db, "e2");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        Assert.Equal(HttpStatusCode.Forbidden, (await DecisionHttp.Decide(factory, "alpha-sup-2", item, "approve")).StatusCode);
        Assert.Equal(0, factory.UserIdentity.CallCount);

        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);
        Assert.Equal(1, factory.UserIdentity.CallCount);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);
        Assert.Equal(1, factory.UserIdentity.CallCount);
    }

    [Fact]
    public async Task Sentinel_rationale_never_appears_in_logs_problems_events_or_responses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (factory, _) = Build(db);
        await using var _f = factory;
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "e3");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        var sentinel = DecisionHttp.Rationale;

        var scan = new SentinelScan();
        await scan.AddResponseAsync("conflict-claimant", await DecisionHttp.Decide(factory, "alpha-sup-2", item, "approve", sentinel));
        await scan.AddResponseAsync("invalid-decision", await DecisionHttp.DecideRaw(factory, "alpha-sup-1", item, "{\"decision\":\"defer\",\"rationale\":\"" + sentinel + "\"}"));
        await scan.AddResponseAsync("extra-member", await DecisionHttp.DecideRaw(factory, "alpha-sup-1", item, "{\"decision\":\"approve\",\"rationale\":\"" + sentinel + "\",\"decided_by\":\"x\"}"));
        await scan.AddResponseAsync("decided", await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve", sentinel));
        await scan.AddResponseAsync("repeat", await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve", sentinel));
        await scan.AddResponseAsync("conflict", await DecisionHttp.Decide(factory, "alpha-sup-1", item, "reject", sentinel));
        using (var c = factory.CreateReviewerClient("alpha-sup-1"))
        {
            await scan.AddResponseAsync("events", await c.GetAsync($"/v1/queue/items/{item}/events", Ct));
            await scan.AddResponseAsync("get", await c.GetAsync($"/v1/queue/items/{item}", Ct));
        }

        scan.AddSource("logs", factory.Logs.Text);
        scan.AddSource("queue events", await DecisionHttp.Scalar<string>(db, "select coalesce(string_agg(detail::text, ' '), '') from queue_item_event where item_id = @i", ("i", item)) ?? "");
        scan.AddSource("proposal events", await DecisionHttp.Scalar<string>(db, "select coalesce(string_agg(detail::text, ' '), '') from proposal_event where proposal_id = @p", ("p", proposal)) ?? "");
        scan.AssertAbsent(sentinel);
        Assert.Equal(sentinel, await DecisionHttp.Scalar<string>(db, "select rationale from queue_item where item_id = @i", ("i", item)));
    }
}
