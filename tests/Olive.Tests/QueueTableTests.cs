using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public class QueueTableTests(PostgresFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Runs one statement as olive_app with the tenant set and returns the SQLSTATE of the failure.
    private static async Task<string> SqlStateAsync(string cs, string sql, Guid? tenant = null)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", (tenant ?? TestPaths.TenantAlpha).ToString());
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        return ex.SqlState;
    }

    private static string InsertItem(Guid proposalId, string subject, string payload, string kind = "action_approval", string producer = "olive", string producerRef = "") =>
        "insert into queue_item (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, proposal_id) " +
        $"values ('{TestPaths.TenantAlpha}', gen_random_uuid(), '{kind}', 'proposal', '{producer}', '{(producerRef == "" ? proposalId.ToString() : producerRef)}', '{subject}', '{payload}', '{proposalId}')";

    [Fact]
    public async Task QUE_01_refs_must_be_the_proposal_reference_scheme()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        var good = $"olive:proposal/{p}";
        foreach (var (subject, payload) in new[]
        {
            (good, "{\"body\":\"inline\"}"),
            (good, "payload://abc"),
            ("bluto:candidate/" + p, "bluto:candidate/" + p),
            (good, $"olive:proposal/{Guid.NewGuid()}"),
            (good, good + "x"),
        })
            Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertItem(p, subject, payload)));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertItem(p, good, good, producerRef: Guid.NewGuid().ToString())));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertItem(p, good, good, producer: "bluto")));
    }

    [Fact]
    public async Task QUE_01_second_item_for_same_proposal_is_refused()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await db.SeedQueueItemAsync(TestPaths.TenantAlpha, p);
        var good = $"olive:proposal/{p}";
        Assert.Equal("23505", await SqlStateAsync(db.AppConnectionString, InsertItem(p, good, good)));
    }

    [Fact]
    public async Task QUE_09_app_cannot_update_or_delete_queue_item_event()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await db.SeedQueueItemAsync(TestPaths.TenantAlpha, p);
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "update queue_item_event set actor = 'x'"));
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "delete from queue_item_event"));
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "truncate queue_item_event"));
    }

    [Theory]
    [InlineData("state = 'decided', claimed_by = 'rev', claimed_at = now(), decision = 'approve', decided_by = 'rev', decided_at = now(), auth_source = 'stub'")]
    [InlineData("state = 'decided', claimed_by = 'rev', claimed_at = now(), decision = 'approve', decided_by = 'rev', decided_at = now(), auth_source = 'stub', rationale = ''")]
    [InlineData("state = 'decided', claimed_by = 'rev', claimed_at = now(), decision = 'approve', decided_by = 'rev', decided_at = now(), auth_source = 'stub', rationale = E' \\n\\t '")]
    [InlineData("state = 'decided', claimed_by = 'rev', claimed_at = now(), decision = 'approve', decided_by = 'other', decided_at = now(), auth_source = 'stub', rationale = 'ok'")]
    [InlineData("state = 'decided', claimed_by = 'rev', claimed_at = now(), decision = 'approve', decided_by = 'rev', decided_at = now(), rationale = 'ok'")]
    [InlineData("state = 'decided', claimed_by = 'rev', claimed_at = now(), decision = 'accept', decided_by = 'rev', decided_at = now(), auth_source = 'stub', rationale = 'ok'")]
    [InlineData("state = 'decided', decision = 'approve', decided_by = 'rev', decided_at = now(), auth_source = 'stub', rationale = 'ok'")]
    public async Task QUE_07_decided_shape_is_enforced_by_the_database(string set)
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await db.SeedQueueItemAsync(TestPaths.TenantAlpha, p);
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, $"update queue_item set {set}"));
    }

    [Fact]
    public async Task QUE_07_complete_decision_is_accepted()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await db.SeedQueueItemAsync(TestPaths.TenantAlpha, p);
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", TestPaths.TenantAlpha.ToString());
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var upd = new NpgsqlCommand(
            "update queue_item set state = 'decided', claimed_by = 'rev', claimed_at = now(), decision = 'approve', decided_by = 'rev', decided_at = now(), auth_source = 'stub', rationale = 'looks right'",
            conn, tx);
        Assert.Equal(1, await upd.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task QUE_09_queue_item_event_without_tenant_setting_raises()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            $"insert into queue_item_event (tenant_id, event_id, item_id, event_type, to_state, actor) values ('{TestPaths.TenantAlpha}', gen_random_uuid(), gen_random_uuid(), 'created', 'open', 'x')", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Contains(ex.SqlState, new[] { "42704", "22P02" });
    }
}
