using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Olive.Application;
using Olive.Domain;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class RateLimitTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Alpha = ApiProposalTests.Alpha;
    private static readonly Guid Beta = ApiProposalTests.Beta;

    private static OliveApiFactory Factory(TestDatabase db, int limit, int window = 3600, string purposes = "[servicing, collections]") =>
        new(db, configureServices: s => s.AddSingleton(OliveApiFactory.CatalogWithRate(limit, window, purposes)));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string runId)
    {
        var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.BodyFor(runId)), Ct);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        return (r.StatusCode, body);
    }

    private static Task<long> CountAsync(TestDatabase db, Guid tenant, string where) =>
        ApiProposalTests.ReaderScalarAsync<long>(db, tenant, "select count(*) from proposal where " + where);

    private static NewProposal Sample(string agent, string run, string action = "crm.case_note@1") => new(
        agent, "key-1", "user-1", "servicing", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
        "nono-1", run, action, "party://p1", "{\"a\":1}", "because", "trace://t1",
        TraceVerification: "fixture", TraceSchemaVersion: "ate/1.0", ExpiresAt: new DateTimeOffset(2030, 1, 2, 11, 4, 5, TimeSpan.Zero));

    private static PolicyVerdict Passing() =>
        PolicyEvaluator.EvaluateCatalogAndPurpose(true, ["servicing"], "servicing").Then(PolicyEvaluator.Entitlement, true, "entitlement_missing");

    private static PostgresProposalStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)));

    [Fact]
    public async Task POL_03_accepted_proposal_is_awaiting_approval_with_allowed_event_and_expiry()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 3);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var (status, body) = await PostAsync(client, "r1");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("awaiting_approval", body.GetProperty("state").GetString());
        var id = body.GetProperty("proposal_id").GetGuid();
        Assert.Equal("policy_evaluated,submitted,transition", await ApiProposalTests.ReaderScalarAsync<string>(db, Alpha, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", ("p", id)));
        var detail = await ApiProposalTests.ReaderScalarAsync<string>(db, Alpha, "select detail::text from proposal_event where proposal_id = @p and event_type = 'policy_evaluated'", ("p", id));
        using var doc = JsonDocument.Parse(detail!);
        Assert.Equal("allowed", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(["catalog", "purpose", "entitlement", "rate"], doc.RootElement.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.All(doc.RootElement.GetProperty("checks").EnumerateArray(), c => Assert.Equal("pass", c.GetProperty("result").GetString()));
        Assert.Equal(1, await ApiProposalTests.ReaderScalarAsync<long>(db, Alpha, "select count(*) from proposal where proposal_id = @p and expires_at is not null", ("p", id)));
    }

    [Fact]
    public async Task POL_03_limit_plus_one_is_stored_rate_limited_201_and_replay_is_200_same_reason()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 3);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        for (var i = 0; i < 3; i++)
        {
            var (s, b) = await PostAsync(client, "ok" + i);
            Assert.Equal(HttpStatusCode.Created, s);
            Assert.Equal("awaiting_approval", b.GetProperty("state").GetString());
        }

        var (status, body) = await PostAsync(client, "over");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("refused_policy", body.GetProperty("state").GetString());
        Assert.Equal("rate_limited", body.GetProperty("refusal_reason").GetString());
        var id = body.GetProperty("proposal_id").GetGuid();
        Assert.Equal(1, await ApiProposalTests.ReaderScalarAsync<long>(db, Alpha, "select count(*) from proposal where proposal_id = @p and expires_at is null and refusal_reason = 'rate_limited'", ("p", id)));
        Assert.Equal("policy_evaluated,policy_refused,submitted", await ApiProposalTests.ReaderScalarAsync<string>(db, Alpha, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", ("p", id)));
        var detail = await ApiProposalTests.ReaderScalarAsync<string>(db, Alpha, "select detail::text from proposal_event where proposal_id = @p and event_type = 'policy_evaluated'", ("p", id));
        Assert.Contains("\"rate\"", detail);
        Assert.Contains("\"fail\"", detail);

        var (replayStatus, replay) = await PostAsync(client, "over");
        Assert.Equal(HttpStatusCode.OK, replayStatus);
        Assert.Equal(id, replay.GetProperty("proposal_id").GetGuid());
        Assert.Equal("rate_limited", replay.GetProperty("refusal_reason").GetString());
        Assert.Equal(4, await CountAsync(db, Alpha, "true"));
    }

    [Fact]
    public async Task POL_03_refusals_do_not_consume_limit()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 2);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var store = Store(db);

        // Refusals in the same (tenant, agent, action) scope, written through the refusal path.
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await store.InsertRefusedAsync(Alpha, Sample("liam", "refused" + i), "purpose_not_permitted", RefusalKind.Policy, Ct, "{}"));
        }

        // Unknown action refusals through the endpoint.
        for (var i = 0; i < 3; i++)
        {
            var r = await client.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.BodyFor("unk" + i).Replace("crm.case_note@1", "crm.unknown@1")), Ct);
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        }

        Assert.Equal("awaiting_approval", (await PostAsync(client, "v1")).Body.GetProperty("state").GetString());
        Assert.Equal("awaiting_approval", (await PostAsync(client, "v2")).Body.GetProperty("state").GetString());
        var third = (await PostAsync(client, "v3")).Body;
        Assert.Equal("refused_policy", third.GetProperty("state").GetString());
        Assert.Equal("rate_limited", third.GetProperty("refusal_reason").GetString());
    }

    [Fact]
    public async Task POL_03_concurrent_submissions_accept_exactly_limit()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 3);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => PostAsync(client, "par" + i), Ct)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Equal(3, await CountAsync(db, Alpha, "state = 'awaiting_approval'"));
        Assert.Equal(9, await CountAsync(db, Alpha, "state = 'refused_policy' and refusal_reason = 'rate_limited'"));
        Assert.Equal(3, results.Count(r => r.Body.GetProperty("state").GetString() == "awaiting_approval"));
        Assert.Equal(9, results.Count(r => r.Body.TryGetProperty("refusal_reason", out var rr) && rr.ValueKind == JsonValueKind.String));
    }

    [Fact]
    public async Task POL_03_window_slides()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 2, window: 60);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        await PostAsync(client, "w1");
        await PostAsync(client, "w2");
        Assert.Equal("refused_policy", (await PostAsync(client, "w3")).Body.GetProperty("state").GetString());

        await db.AdminExecAsync("update proposal set created_at = now() - interval '120 seconds'");
        Assert.Equal("awaiting_approval", (await PostAsync(client, "w4")).Body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task POL_03_scope_is_per_tenant()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 1);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var beta = factory.CreateKeyClient(ApiProposalTests.BetaKey);

        Assert.Equal("awaiting_approval", (await PostAsync(alpha, "a1")).Body.GetProperty("state").GetString());
        Assert.Equal("refused_policy", (await PostAsync(alpha, "a2")).Body.GetProperty("state").GetString());
        Assert.Equal("awaiting_approval", (await PostAsync(beta, "b1")).Body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task POL_03_scope_is_per_agent_and_per_action_at_the_store()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);

        Assert.Equal("awaiting_approval", (await store.InsertEvaluatedAsync(Alpha, Sample("agent-a", "r1"), 1, 3600, Passing(), Ct))!.State);
        Assert.Equal("refused_policy", (await store.InsertEvaluatedAsync(Alpha, Sample("agent-a", "r2"), 1, 3600, Passing(), Ct))!.State);
        Assert.Equal("awaiting_approval", (await store.InsertEvaluatedAsync(Alpha, Sample("agent-b", "r1"), 1, 3600, Passing(), Ct))!.State);
        Assert.Equal("awaiting_approval", (await store.InsertEvaluatedAsync(Alpha, Sample("agent-a", "r3", "crm.other@1"), 1, 3600, Passing(), Ct))!.State);
        Assert.Equal("awaiting_approval", (await store.InsertEvaluatedAsync(Beta, Sample("agent-a", "r1"), 1, 3600, Passing(), Ct))!.State);
    }

    [Fact]
    public async Task POL_03_duplicate_run_returns_null_and_does_not_count()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        Assert.NotNull(await store.InsertEvaluatedAsync(Alpha, Sample("agent-a", "same"), 5, 3600, Passing(), Ct));
        Assert.Null(await store.InsertEvaluatedAsync(Alpha, Sample("agent-a", "same"), 5, 3600, Passing(), Ct));
        Assert.Equal(1, await CountAsync(db, Alpha, "true"));
    }

    [Fact]
    public async Task POL_03_lock_not_taken_for_refused_proposal()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 3, purposes: "[collections]");
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        await using var holder = new NpgsqlConnection(db.AppConnectionString);
        await holder.OpenAsync(Ct);
        await using var tx = await holder.BeginTransactionAsync(Ct);
        await using (var lockCmd = new NpgsqlCommand(ProposalSql.LockRate, holder, tx))
        {
            lockCmd.Parameters.AddWithValue("tenant_id", Alpha);
            lockCmd.Parameters.AddWithValue("agent_id", "liam");
            lockCmd.Parameters.AddWithValue("action_type", "crm.case_note@1");
            await lockCmd.ExecuteNonQueryAsync(Ct);
        }

        var refused = PostAsync(client, "refused-while-locked");
        Assert.Same(refused, await Task.WhenAny(refused, Task.Delay(TimeSpan.FromSeconds(20), Ct)));
        Assert.Equal("purpose_not_permitted", (await refused).Body.GetProperty("refusal_reason").GetString());
        await tx.RollbackAsync(Ct);
    }

    [Fact]
    public async Task POL_03_accepted_path_waits_for_the_rate_lock()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Factory(db, 3);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        await using var holder = new NpgsqlConnection(db.AppConnectionString);
        await holder.OpenAsync(Ct);
        await using var tx = await holder.BeginTransactionAsync(Ct);
        await using (var lockCmd = new NpgsqlCommand(ProposalSql.LockRate, holder, tx))
        {
            lockCmd.Parameters.AddWithValue("tenant_id", Alpha);
            lockCmd.Parameters.AddWithValue("agent_id", "liam");
            lockCmd.Parameters.AddWithValue("action_type", "crm.case_note@1");
            await lockCmd.ExecuteNonQueryAsync(Ct);
        }

        var pending = PostAsync(client, "blocked");
        await Task.Delay(1500, Ct);
        Assert.False(pending.IsCompleted);
        await tx.RollbackAsync(Ct);
        Assert.Equal("awaiting_approval", (await pending).Body.GetProperty("state").GetString());
    }

    // WR-01: scopes that a plain separator join would merge must take different locks.
    [Theory]
    [InlineData("a|b", "c", "a", "b|c")]
    [InlineData("a:1", "x", "a", "1:x")]
    [InlineData("liam", "crm.case_note@1", "liam:", "crm.case_note@1")]
    public async Task POL_03_rate_lock_key_is_unambiguous_across_scopes(string agentA, string actionA, string agentB, string actionB)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync(Ct);

        async Task<long> KeyAsync(string agent, string action)
        {
            await using var cmd = new NpgsqlCommand("select " + ProposalSql.RateLockKey, conn);
            cmd.Parameters.AddWithValue("tenant_id", Alpha);
            cmd.Parameters.AddWithValue("agent_id", agent);
            cmd.Parameters.AddWithValue("action_type", action);
            return (long)(await cmd.ExecuteScalarAsync(Ct))!;
        }

        Assert.NotEqual(await KeyAsync(agentA, actionA), await KeyAsync(agentB, actionB));
        Assert.Equal(await KeyAsync(agentA, actionA), await KeyAsync(agentA, actionA));
    }

    [Fact]
    public async Task POL_03_a1_hashtextextended_available()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("select hashtextextended('x', 0)", conn);
        Assert.IsType<long>(await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task POL_03_a2_default_isolation_is_read_committed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var uow = new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString));
        var level = await uow.ExecuteAsync(Alpha, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand("select current_setting('transaction_isolation')", conn, tx);
            return (string)(await cmd.ExecuteScalarAsync(Ct))!;
        }, Ct);
        Assert.Equal("read committed", level);
    }

    [Fact]
    public void POL_03_append_rate_records_the_check_and_reason()
    {
        var pass = PolicyEvaluator.AppendRate(Passing(), true);
        Assert.True(pass.Allowed);
        Assert.Equal(new PolicyCheck("rate", "pass"), pass.Checks[^1]);
        var fail = PolicyEvaluator.AppendRate(Passing(), false);
        Assert.False(fail.Allowed);
        Assert.Equal("rate_limited", fail.Reason);
        Assert.Equal(new PolicyCheck("rate", "fail"), fail.Checks[^1]);
    }
}
