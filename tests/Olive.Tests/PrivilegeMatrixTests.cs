using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public class PrivilegeMatrixTests(PostgresFixture fx)
{
    private static readonly string[] AllPrivileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE"];

    // proposal UPDATE is column-level (state from migration 0002, refusal_reason from 0006) and proposal INSERT is column-level (migrations 0003 and 0004),
    // so has_table_privilege reports false for both on olive_app.
    // Phases 5 and 6 append queue_item, queue_item_event, execution_ledger here (DB-03/DB-04 text names five tables; D-03 limits 0001 to two).
    private static readonly (string Table, string Role, string[] Granted)[] Expected =
    [
        ("proposal", "olive_app", ["SELECT"]),
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
    public async Task DB_04_app_update_is_limited_to_state_and_refusal_reason_columns()
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
            Assert.Equal(col is "state" or "refusal_reason", (bool)(await cmd.ExecuteScalarAsync())!);
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

    private static readonly string[] InsertableColumns =
        ["tenant_id", "proposal_id", "agent_id", "key_id", "on_behalf_of", "purpose", "key_expires_at", "nono_session_id", "run_id", "action_type", "party_ref", "parameters", "justification", "trace_ref", "canonical_body", "body_sha256", "proposal_hash", "trace_verification", "trace_schema_version"];

    private static string InsertSql(string extraCol, string extraVal) =>
        $"insert into proposal (tenant_id, proposal_id, agent_id, key_id, on_behalf_of, purpose, key_expires_at, run_id{extraCol}) " +
        $"values ('{TestPaths.TenantAlpha}', gen_random_uuid(), 'a', 'k', 'u', 'p', now(), 'r-ins'{extraVal})";

    [Fact]
    public async Task DB_04_app_cannot_insert_non_default_state()
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, InsertSql(", state", ", 'approved'")));
    }

    [Fact]
    public async Task DB_04_app_cannot_insert_created_at()
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, InsertSql(", created_at", ", '2020-01-01'")));
    }

    [Fact]
    public async Task DB_04_app_insert_is_limited_to_application_columns()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync();
        var cols = new List<string>();
        await using (var q = new NpgsqlCommand("select attname from pg_attribute where attrelid = 'public.proposal'::regclass and attnum > 0 and not attisdropped", conn))
        await using (var r = await q.ExecuteReaderAsync())
            while (await r.ReadAsync()) cols.Add(r.GetString(0));
        Assert.Contains("state", cols);
        Assert.Contains("created_at", cols);
        foreach (var col in cols)
        {
            await using var cmd = new NpgsqlCommand("select has_column_privilege('olive_app', 'public.proposal', @c, 'INSERT')", conn);
            cmd.Parameters.AddWithValue("c", col);
            Assert.Equal(InsertableColumns.Contains(col), (bool)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task DB_04_app_can_insert_proposal_with_default_state()
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
        await using var sel = new NpgsqlCommand("select state from proposal", conn, tx);
        Assert.Equal("proposed", (string)(await sel.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DB_04_app_cannot_create_objects()
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "create table x (a int)"));
    }

    private static readonly string Hex64 = new('a', 64);

    // SHA-256 of the UTF-8 bytes of "{}", the canonical body these inserts store (0005 binds the two).
    private const string EmptyObjectSha = "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";

    [Theory]
    [InlineData(", body_sha256", ", 'zz'")]
    [InlineData(", proposal_hash", ", 'zz'")]
    [InlineData(", canonical_body", ", '{}'")]
    [InlineData(", canonical_body, body_sha256", ", '{}', 'aa'")]
    [InlineData(", canonical_body, body_sha256, proposal_hash", ", '{}', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData(", canonical_body, body_sha256, proposal_hash", ", '{ }', '44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    public async Task DB_04_hash_columns_enforce_format_and_all_or_none(string cols, string vals)
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertSql(cols, vals)));
    }

    [Fact]
    public async Task TRC_09_app_cannot_update_trace_verification()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "update proposal set trace_verification = 'fixture'"));
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "update proposal set trace_schema_version = 'ate/1.0'"));
    }

    [Fact]
    public async Task TRC_03_app_cannot_insert_refusal_reason()
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, InsertSql(", refusal_reason", ", 'trace_not_found'")));
    }

    [Theory]
    [InlineData(", trace_verification", ", 'mismatch'")]
    [InlineData(", trace_schema_version", ", 'ate/1.0'")]
    [InlineData(", trace_verification, trace_schema_version", ", 'fixture', 'v1'")]
    public async Task TRC_09_trace_columns_enforce_values_and_format(string cols, string vals)
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, InsertSql(cols, vals)));
    }

    [Fact]
    public async Task TRC_09_app_can_insert_valid_trace_columns()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", TestPaths.TenantAlpha.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(InsertSql(", trace_verification, trace_schema_version", ", 'fixture', 'ate/1.0'"), conn, tx);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task TRC_09_refusal_state_and_reason_must_agree()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, "update proposal set state = 'refused_policy'"));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, "update proposal set refusal_reason = 'trace_not_found'"));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, "update proposal set state = 'refused_policy', refusal_reason = 'Bad Reason'"));
    }

    [Fact]
    public async Task DB_04_app_can_insert_all_three_hash_columns_and_cannot_update_them()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var ins = InsertSql(", canonical_body, body_sha256, proposal_hash", $", '{{}}', '{EmptyObjectSha}', '{Hex64}'");
        await using var conn = new NpgsqlConnection(db.AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", TestPaths.TenantAlpha.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using (var cmd = new NpgsqlCommand(ins, conn, tx))
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        await tx.CommitAsync();
        foreach (var col in new[] { "canonical_body", "body_sha256", "proposal_hash" })
            Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, $"update proposal set {col} = null"));
    }
}
