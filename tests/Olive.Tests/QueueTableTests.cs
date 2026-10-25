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

    private static string InsertIdentity(Guid producerRef, string? bodySha) =>
        "insert into queue_item (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, ruleset_version, body_sha256) " +
        $"values ('{TestPaths.TenantAlpha}', gen_random_uuid(), 'identity_review', 'candidate_pair', 'bluto', '{producerRef}', 'bluto:candidate/{producerRef}', " +
        $"'bluto:review-evidence/{producerRef}', 'test-ruleset', {(bodySha is null ? "null" : $"'{bodySha}'")})";

    private static async Task InsertOkAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", TestPaths.TenantAlpha.ToString());
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(Ct));
        await tx.CommitAsync(Ct);
    }

    [Fact]
    public async Task IRQ_01_event_type_check_has_the_assumed_constraint_name()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("select count(*) from pg_constraint where conrelid = 'queue_item_event'::regclass and conname = 'queue_item_event_event_type_check'", conn);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task IRQ_01_identity_item_requires_body_sha256()
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertIdentity(Guid.NewGuid(), null)));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertIdentity(Guid.NewGuid(), "ABC")));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertIdentity(Guid.NewGuid(), new string('a', 63))));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertIdentity(Guid.NewGuid(), new string('A', 64))));
        await InsertOkAsync(db.AppConnectionString, InsertIdentity(Guid.NewGuid(), new string('a', 64)));
    }

    [Fact]
    public async Task IRQ_01_action_approval_item_does_not_need_body_sha256()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await db.SeedQueueItemAsync(TestPaths.TenantAlpha, p);
    }

    [Fact]
    public async Task IRQ_02_event_type_accepts_deferred_and_rejects_unknown()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid());
        string Ev(string type) =>
            "insert into queue_item_event (tenant_id, event_id, item_id, event_type, to_state, actor) " +
            $"values ('{TestPaths.TenantAlpha}', gen_random_uuid(), '{item}', '{type}', 'open', 'rev')";
        await InsertOkAsync(db.AppConnectionString, Ev("deferred"));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, Ev("bogus")));
    }

    [Fact]
    public async Task IRQ_03_decided_poll_index_exists()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("select count(*) from pg_indexes where indexname = 'queue_item_decided_poll_idx'", conn);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task IRQ_01_seed_identity_item_supports_each_state_shape()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid());
        await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid(), "claimed", claimedBy: "rev");
        await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid(), "decided", claimedBy: "rev", decision: "accept");
        await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid(), "withdrawn", withdrawnReason: "superseded");
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
