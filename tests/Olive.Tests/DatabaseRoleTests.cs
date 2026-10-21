using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public class DatabaseRoleTests(PostgresFixture fx)
{
    private static readonly string[] Roles = ["olive_owner", "olive_migrator", "olive_app", "olive_reader"];

    private async Task<List<(string Name, bool Login, bool Super, bool Bypass, bool CreateRole, bool CreateDb, bool Repl)>> ReadRolesAsync()
    {
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString("postgres"));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select rolname, rolcanlogin, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb, rolreplication from pg_roles where rolname like 'olive\\_%' order by rolname", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<(string, bool, bool, bool, bool, bool, bool)>();
        while (await r.ReadAsync())
        {
            list.Add((r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6)));
        }

        return list;
    }

    [Fact]
    public async Task DB_01_four_roles_exist_with_expected_login_and_no_elevated_flags()
    {
        var roles = await ReadRolesAsync();
        Assert.Equal(Roles.Order(), roles.Select(r => r.Name).Where(n => Roles.Contains(n)).Order());
        foreach (var r in roles.Where(r => Roles.Contains(r.Name)))
        {
            Assert.Equal(r.Name != "olive_owner", r.Login);
            Assert.False(r.Super);
            Assert.False(r.Bypass);
            Assert.False(r.CreateRole);
            Assert.False(r.CreateDb);
            Assert.False(r.Repl);
        }
    }

    [Fact]
    public async Task DB_01_migrator_is_member_of_owner_and_app_reader_are_not()
    {
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString("postgres"));
        await conn.OpenAsync();
        foreach (var (role, expected) in new[] { ("olive_migrator", true), ("olive_app", false), ("olive_reader", false) })
        {
            await using var cmd = new NpgsqlCommand($"select pg_has_role('{role}', 'olive_owner', 'MEMBER')", conn);
            Assert.Equal(expected, (bool)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task DB_01_rerunning_bootstrap_succeeds_and_restores_flags()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(TestPaths.Root, "sql", "bootstrap", "001_roles.sql"));
        await using var conn = new NpgsqlConnection(fx.AdminConnectionString("postgres"));
        await conn.OpenAsync();
        try
        {
            // olive_reader is flipped (not olive_app) and restored in finally to limit the effect on parallel tests.
            await using (var flip = new NpgsqlCommand("alter role olive_reader bypassrls", conn))
            {
                await flip.ExecuteNonQueryAsync();
            }

            await using var rerun = new NpgsqlCommand(script, conn);
            await rerun.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var restore = new NpgsqlCommand("alter role olive_reader nobypassrls", conn);
            await restore.ExecuteNonQueryAsync();
        }

        var reader = (await ReadRolesAsync()).Single(r => r.Name == "olive_reader");
        Assert.False(reader.Bypass);
    }

    [Fact]
    public void DB_01_database_script_sets_owner_and_connect_grants()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Root, "sql", "bootstrap", "002_database.sql"));
        Assert.Contains("owner olive_owner", text);
        Assert.Contains("revoke all on database olive from public", text);
        Assert.Contains("grant connect", text);
    }
}
