using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public class PrivilegeMatrixTests(PostgresFixture fx)
{
    private static readonly string[] AllPrivileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE"];

    // proposal UPDATE is column-level (state only, migration 0002), so has_table_privilege reports false for UPDATE.
    // Phases 5 and 6 append queue_item, queue_item_event, execution_ledger here (DB-03/DB-04 text names five tables; D-03 limits 0001 to two).
    private static readonly (string Table, string Role, string[] Granted)[] Expected =
    [
        ("proposal", "olive_app", ["SELECT", "INSERT"]),
        ("proposal", "olive_reader", ["SELECT"]),
        ("proposal_event", "olive_app", ["SELECT", "INSERT"]),
        ("proposal_event", "olive_reader", ["SELECT"]),
    ];

    [Fact]
    public async Task DB_04_privilege_matrix_equals_expected()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync();
        foreach (var (table, role, granted) in Expected)
        {
            foreach (var priv in AllPrivileges)
            {
                await using var cmd = new NpgsqlCommand("select has_table_privilege(@r, @t, @p)", conn);
                cmd.Parameters.AddWithValue("r", role);
                cmd.Parameters.AddWithValue("t", "public." + table);
                cmd.Parameters.AddWithValue("p", priv);
                var actual = (bool)(await cmd.ExecuteScalarAsync())!;
                Assert.True(granted.Contains(priv) == actual, $"{role} {priv} on {table}: expected {granted.Contains(priv)}");
            }
        }
    }

    private static async Task<string> SqlStateAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", TestPaths.TenantAlpha.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        return ex.SqlState;
    }

    [Fact]
    public async Task DB_04_app_update_is_limited_to_state_column()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync();
        var cols = new List<string>();
        await using (var q = new NpgsqlCommand("select attname from pg_attribute where attrelid = 'public.proposal'::regclass and attnum > 0 and not attisdropped", conn))
        await using (var r = await q.ExecuteReaderAsync())
            while (await r.ReadAsync()) cols.Add(r.GetString(0));
        Assert.Contains("state", cols);
        foreach (var col in cols)
        {
            await using var cmd = new NpgsqlCommand("select has_column_privilege('olive_app', 'public.proposal', @c, 'UPDATE')", conn);
            cmd.Parameters.AddWithValue("c", col);
            Assert.Equal(col == "state", (bool)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task DB_04_app_cannot_update_immutable_proposal_columns()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        foreach (var sql in new[]
        {
            "update proposal set parameters = '{}'::jsonb",
            "update proposal set tenant_id = gen_random_uuid()",
            "update proposal set key_id = 'x'",
            "update proposal set run_id = 'x'",
        })
            Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, sql));
    }

    [Fact]
    public async Task DB_04_app_can_still_update_proposal_state()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", TestPaths.TenantAlpha.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var upd = new NpgsqlCommand("update proposal set state = 'approved'", conn, tx);
        Assert.Equal(1, await upd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task DB_04_app_cannot_update_proposal_event()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "update proposal_event set actor = 'x'"));
    }

    [Fact]
    public async Task DB_04_app_cannot_delete_proposal_event()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "delete from proposal_event"));
    }

    [Fact]
    public async Task DB_04_reader_cannot_insert_proposal()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var sql = "insert into proposal (tenant_id, proposal_id, agent_id, key_id, on_behalf_of, purpose, key_expires_at, run_id) " +
                  $"values ('{TestPaths.TenantAlpha}', gen_random_uuid(), 'a', 'k', 'u', 'p', now(), 'r')";
        Assert.Equal("42501", await SqlStateAsync(db.ReaderConnectionString, sql));
    }

    [Fact]
    public async Task DB_04_app_cannot_create_objects()
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "create table x (a int)"));
    }
}
