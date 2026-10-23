using System.Net;
using System.Text.Json;
using Olive.Application;

namespace Olive.Tests;

internal static class DecisionHttp
{
    internal static readonly Guid Alpha = TestPaths.TenantAlpha;
    internal const string Rationale = "RATIONALE-SENTINEL-9917";
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Submits a proposal as the agent, points its on_behalf_of at the given UPN, and returns the queue item id and proposal id.
    internal static async Task<(Guid Item, Guid Proposal)> SubmitAsync(OliveApiFactory factory, TestDatabase db, string run, string onBehalfOf = "analyst.alpha@example.test", int? expiresIn = null)
    {
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor(run);
        if (expiresIn is { } e) { body = body.TrimEnd('}') + ",\"expires_in_seconds\":" + e + "}"; }
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var proposal = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();
        await db.AdminExecAsync($"update proposal set on_behalf_of = '{onBehalfOf}' where proposal_id = '{proposal}'");
        using var rev = factory.CreateReviewerClient("alpha-sup-1");
        var items = await ReviewerTenantTests.ListAsync(rev);
        var item = items.Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid();
        return (item, proposal);
    }

    internal static async Task ClaimAsync(OliveApiFactory factory, string reviewer, Guid item)
    {
        using var c = factory.CreateReviewerClient(reviewer);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/v1/queue/items/{item}/claim", null, Ct)).StatusCode);
    }

    internal static Task<HttpResponseMessage> DecideRaw(OliveApiFactory factory, string reviewer, Guid item, string json)
    {
        var c = factory.CreateReviewerClient(reviewer);
        return c.PostAsync($"/v1/queue/items/{item}/decision", ApiProposalTests.Json(json), Ct);
    }

    internal static Task<HttpResponseMessage> Decide(OliveApiFactory factory, string reviewer, Guid item, string decision, string rationale = Rationale) =>
        DecideRaw(factory, reviewer, item, JsonSerializer.Serialize(new { decision, rationale }));

    internal static async Task<string> ReasonOf(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    internal static Task<T?> Scalar<T>(TestDatabase db, string sql, params (string, object)[] args) => ApiProposalTests.ReaderScalarAsync<T>(db, Alpha, sql, args);

    internal static Task<string?> ProposalState(TestDatabase db, Guid proposal) =>
        Scalar<string>(db, "select state from proposal where proposal_id = @p", ("p", proposal));

    internal static Task<long> EventCount(TestDatabase db, Guid item, string type) =>
        Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i and event_type = @t", ("i", item), ("t", type))!;
}

[Trait("Category", "Integration")]
public sealed class QueueDecisionTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QUE_07_valid_approve_by_claimant_who_is_not_the_requester_is_200()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "q7a");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        var response = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("decided", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("approve", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", doc.RootElement.GetProperty("decided_by").GetString());
        Assert.DoesNotContain(DecisionHttp.Rationale, text);
        Assert.Equal("approved", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task QUE_07_non_claimant_unclaimed_and_unknown_items_are_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "q7b");

        var unclaimed = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");
        Assert.Equal(HttpStatusCode.Conflict, unclaimed.StatusCode);
        Assert.Equal("item_not_claimed", await DecisionHttp.ReasonOf(unclaimed));

        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        var other = await DecisionHttp.Decide(factory, "alpha-sup-2", item, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("not_claimant", await DecisionHttp.ReasonOf(other));

        var missing = await DecisionHttp.Decide(factory, "alpha-sup-1", Guid.NewGuid(), "approve");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var cross = await DecisionHttp.Decide(factory, "beta-sup", item, "approve");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
    }

    [Theory]
    [InlineData("{\"decision\":\"approve\",\"rationale\":\"\"}")]
    [InlineData("{\"decision\":\"approve\",\"rationale\":\"    \"}")]
    [InlineData("{\"decision\":\"approve\",\"rationale\":\"\\u3000\\u3000\"}")]
    [InlineData("{\"decision\":\"approve\"}")]
    [InlineData("{\"rationale\":\"ok\"}")]
    [InlineData("{\"decision\":\"approve\",\"rationale\":\"ok\",\"decided_by\":\"x\"}")]
    [InlineData("{\"decision\":\"approve\",\"rationale\":\"ok\",\"tenant_id\":\"5325ffe9-5727-4d88-9832-aebabb8d0538\"}")]
    [InlineData("{\"decision\":\"approve\",\"rationale\":\"ok\",\"auth_source\":\"entra\"}")]
    [InlineData("{\"decision\":\"defer\",\"rationale\":\"ok\"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task QUE_07_malformed_or_forbidden_bodies_are_400_and_change_nothing(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "q7c");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        var response = await DecisionHttp.DecideRaw(factory, "alpha-sup-1", item, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await DecisionHttp.ReasonOf(response));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task QUE_07_over_length_rationale_is_400()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "q7d");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        Assert.Equal(HttpStatusCode.BadRequest, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve", new string('x', 4001))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve", new string('x', 4000))).StatusCode);
    }

    [Fact]
    public async Task APR_05_auth_source_is_stub_from_the_claim_and_a_header_cannot_change_it()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "a5");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        using var c = factory.CreateReviewerClient("alpha-sup-1");
        c.DefaultRequestHeaders.Add("X-Auth-Source", "entra");
        var response = await c.PostAsync($"/v1/queue/items/{item}/decision", ApiProposalTests.Json("{\"decision\":\"approve\",\"rationale\":\"ok\"}"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stub", await DecisionHttp.Scalar<string>(db, "select auth_source from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(1L, await DecisionHttp.Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i and event_type = 'decided' and detail->>'auth_source' = 'stub'", ("i", item)));
        Assert.Equal(1L, await DecisionHttp.Scalar<long>(db, "select count(*) from proposal_event e join queue_item q on q.proposal_id = e.proposal_id where q.item_id = @i and e.to_state = 'approved' and e.detail->>'auth_source' = 'stub'", ("i", item)));
    }

    [Fact]
    public async Task Role_claimant_without_the_required_role_cannot_decide()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "role");
        await DecisionHttp.ClaimAsync(factory, "alpha-norole", item);

        var response = await DecisionHttp.Decide(factory, "alpha-norole", item, "approve");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("role_not_permitted", await DecisionHttp.ReasonOf(response));
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task QUE_08_repeat_is_200_without_new_event_different_decision_conflicts_other_reviewer_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "q8");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);

        var first = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");
        var again = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await again.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1L, await DecisionHttp.EventCount(db, item, "decided"));

        var conflict = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "reject");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("decision_conflict", await DecisionHttp.ReasonOf(conflict));

        var other = await DecisionHttp.Decide(factory, "alpha-sup-2", item, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("not_claimant", await DecisionHttp.ReasonOf(other));
        Assert.Equal(1L, await DecisionHttp.EventCount(db, item, "decided"));
    }

    [Fact]
    public async Task APR_04_approve_and_reject_move_the_proposal_and_it_reads_back_over_the_agent_api()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "a4a");
        var (item2, proposal2) = await DecisionHttp.SubmitAsync(factory, db, "a4b");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item2);

        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item2, "reject")).StatusCode);

        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var got = JsonDocument.Parse(await (await agent.GetAsync($"/v1/proposals/{proposal}", Ct)).Content.ReadAsStringAsync(Ct));
        Assert.Equal("approved", got.RootElement.GetProperty("state").GetString());
        Assert.Equal("rejected", await DecisionHttp.ProposalState(db, proposal2));
        Assert.Equal("decided", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task QUE_09_history_lists_every_step_in_seq_order()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "q9");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        using (var c = factory.CreateReviewerClient("alpha-sup-1"))
        {
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/v1/queue/items/{item}/release", null, Ct)).StatusCode);
        }

        await DecisionHttp.ClaimAsync(factory, "alpha-sup-2", item);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-2", item, "reject")).StatusCode);

        var events = await QueueClaimTests.Store(db).EventsAsync(DecisionHttp.Alpha, item, Ct);
        Assert.Equal(["created", "claimed", "released", "claimed", "decided"], events!.Select(e => e.EventType).ToArray());
        Assert.Equal(events.OrderBy(e => e.Seq).Select(e => e.Seq), events.Select(e => e.Seq));
    }
}
