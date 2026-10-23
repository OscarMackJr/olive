using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public class RlsPatternTests(PostgresFixture fx)
{
    // Phases 5 and 6 append queue_item, queue_item_event, execution_ledger here (DB-03/DB-04 text names five tables; D-03 limits 0001 to two).
    private static readonly string[] ExpectedTables = ["proposal", "proposal_event", "queue_item", "queue_item_event"];

    private static async Task<List<string>> DiscoverAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            "select c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname = 'public' and c.relkind = 'r' and c.relname <> 'schema_migrations' order by 1", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync())
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    [Fact]
    public async Task DB_03_expected_tables_are_present()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync();
        var tables = await DiscoverAsync(conn);
        Assert.All(ExpectedTables, t => Assert.Contains(t, tables));
    }

    [Fact]
    public async Task DB_03_every_table_has_not_null_uuid_tenant_id_and_forced_rls()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync();
        foreach (var table in await DiscoverAsync(conn))
        {
            await using var col = new NpgsqlCommand(
                "select data_type, is_nullable from information_schema.columns where table_schema = 'public' and table_name = @t and column_name = 'tenant_id'", conn);
            col.Parameters.AddWithValue("t", table);
            await using (var r = await col.ExecuteReaderAsync())
            {
                Assert.True(await r.ReadAsync(), table + " lacks tenant_id");
                Assert.Equal("uuid", r.GetString(0));
                Assert.Equal("NO", r.GetString(1));
            }

            await using var rls = new NpgsqlCommand("select relrowsecurity, relforcerowsecurity from pg_class where oid = ('public.' || @t)::regclass", conn);
            rls.Parameters.AddWithValue("t", table);
            await using var rr = await rls.ExecuteReaderAsync();
            Assert.True(await rr.ReadAsync());
            Assert.True(rr.GetBoolean(0), table + " RLS not enabled");
            Assert.True(rr.GetBoolean(1), table + " RLS not forced");
        }
    }

    [Fact]
    public async Task DB_03_every_table_has_one_policy_without_missing_ok()
    {
        await using var db = await fx.CreateDatabaseAsync();
        await using var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync();
        foreach (var table in await DiscoverAsync(conn))
        {
            await using var cmd = new NpgsqlCommand("select qual, with_check from pg_policies where schemaname = 'public' and tablename = @t", conn);
            cmd.Parameters.AddWithValue("t", table);
            await using var r = await cmd.ExecuteReaderAsync();
            var rows = new List<(string, string)>();
            while (await r.ReadAsync())
            {
                rows.Add((r.GetString(0), r.GetString(1)));
            }

            var (qual, check) = Assert.Single(rows);
            foreach (var expr in new[] { qual, check })
            {
                Assert.Contains("current_setting('olive.tenant_id'::text)", expr);
                Assert.DoesNotContain("true)", expr);
            }
        }
    }
}
