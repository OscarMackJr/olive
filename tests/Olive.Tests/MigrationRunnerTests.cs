using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public class MigrationRunnerTests(PostgresFixture fx)
{
    private static IReadOnlyList<MigrationFile> Real => MigrationCatalog.LoadEmbedded();

    private static async Task<T> Scalar<T>(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task DB_02_applies_0001_as_owner_and_records_checksum()
    {
        await using var db = await fx.CreateUnmigratedDatabaseAsync();
        var cs = fx.MigratorConnectionString(db.Name);
        var applied = await new MigrationRunner().RunAsync(cs, Real, CancellationToken.None);
        Assert.Equal(["0001", "0002", "0003", "0004", "0005", "0006"], applied);
        Assert.Equal("olive_owner", await Scalar<string>(cs, "select relowner::regrole::text from pg_class where relname = 'proposal'"));
        var expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(TestPaths.Root, "sql", "migrations", "0001_init.sql"))));
        Assert.Equal(expected, await Scalar<string>(cs, "select sha256 from schema_migrations where version = '0001'"));
        Assert.Equal("0001_init.sql", await Scalar<string>(cs, "select filename from schema_migrations where version = '0001'"));
        var expected2 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(TestPaths.Root, "sql", "migrations", "0002_proposal_column_grants.sql"))));
        Assert.Equal(expected2, await Scalar<string>(cs, "select sha256 from schema_migrations where version = '0002'"));
        Assert.Equal("0002_proposal_column_grants.sql", await Scalar<string>(cs, "select filename from schema_migrations where version = '0002'"));
        var expected3 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(TestPaths.Root, "sql", "migrations", "0003_proposal_insert_columns.sql"))));
        Assert.Equal(expected3, await Scalar<string>(cs, "select sha256 from schema_migrations where version = '0003'"));
        Assert.Equal("0003_proposal_insert_columns.sql", await Scalar<string>(cs, "select filename from schema_migrations where version = '0003'"));
        var expected4 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(TestPaths.Root, "sql", "migrations", "0004_proposal_body_hash.sql"))));
        Assert.Equal(expected4, await Scalar<string>(cs, "select sha256 from schema_migrations where version = '0004'"));
        Assert.Equal("0004_proposal_body_hash.sql", await Scalar<string>(cs, "select filename from schema_migrations where version = '0004'"));
        var expected5 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(TestPaths.Root, "sql", "migrations", "0005_proposal_body_hash_binding.sql"))));
        Assert.Equal(expected5, await Scalar<string>(cs, "select sha256 from schema_migrations where version = '0005'"));
        Assert.Equal("0005_proposal_body_hash_binding.sql", await Scalar<string>(cs, "select filename from schema_migrations where version = '0005'"));
        var expected6 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(TestPaths.Root, "sql", "migrations", "0006_trace_verification.sql"))));
        Assert.Equal(expected6, await Scalar<string>(cs, "select sha256 from schema_migrations where version = '0006'"));
        Assert.Equal("0006_trace_verification.sql", await Scalar<string>(cs, "select filename from schema_migrations where version = '0006'"));
    }

    [Fact]
    public async Task DB_02_second_run_applies_nothing()
    {
        await using var db = await fx.CreateUnmigratedDatabaseAsync();
        var cs = fx.MigratorConnectionString(db.Name);
        await new MigrationRunner().RunAsync(cs, Real, CancellationToken.None);
        var again = await new MigrationRunner().RunAsync(cs, Real, CancellationToken.None);
        Assert.Empty(again);
        Assert.Equal((long)Real.Count, await Scalar<long>(cs, "select count(*) from schema_migrations"));
    }

    [Fact]
    public async Task DB_02_changed_bytes_fail_with_checksum_exception()
    {
        await using var db = await fx.CreateUnmigratedDatabaseAsync();
        var cs = fx.MigratorConnectionString(db.Name);
        var files = Real;
        await new MigrationRunner().RunAsync(cs, files, CancellationToken.None);
        var tampered = files.Select((f, i) => i == 0 ? f with { Bytes = [.. f.Bytes, .. Encoding.UTF8.GetBytes("\n-- x\n")] } : f).ToArray();
        var ex = await Assert.ThrowsAsync<MigrationChecksumException>(() => new MigrationRunner().RunAsync(cs, tampered, CancellationToken.None));
        Assert.Contains("0001", ex.Message);
        Assert.DoesNotContain("create table", ex.Message);
    }

    [Fact]
    public async Task DB_02_recorded_version_missing_from_list_is_drift()
    {
        await using var db = await fx.CreateUnmigratedDatabaseAsync();
        var cs = fx.MigratorConnectionString(db.Name);
        await new MigrationRunner().RunAsync(cs, Real, CancellationToken.None);
        var other = new[] { new MigrationFile("0002_other.sql", Encoding.UTF8.GetBytes("select 1;")) };
        await Assert.ThrowsAsync<MigrationDriftException>(() => new MigrationRunner().RunAsync(cs, other, CancellationToken.None));
    }

    [Fact]
    public async Task DB_02_failing_migration_rolls_back()
    {
        await using var db = await fx.CreateUnmigratedDatabaseAsync();
        var cs = fx.MigratorConnectionString(db.Name);
        var bad = new[] { new MigrationFile("0001_bad.sql", Encoding.UTF8.GetBytes("create table partial_t (a int); select 1/0;")) };
        await Assert.ThrowsAnyAsync<PostgresException>(() => new MigrationRunner().RunAsync(cs, bad, CancellationToken.None));
        Assert.Equal(0L, await Scalar<long>(cs, "select count(*) from schema_migrations"));
        Assert.Equal(0L, await Scalar<long>(cs, "select count(*) from pg_class where relname = 'partial_t'"));
    }

    [Fact]
    public async Task DB_02_concurrent_runs_apply_once()
    {
        await using var db = await fx.CreateUnmigratedDatabaseAsync();
        var cs = fx.MigratorConnectionString(db.Name);
        var files = Real;
        var results = await Task.WhenAll(
            new MigrationRunner().RunAsync(cs, files, CancellationToken.None),
            new MigrationRunner().RunAsync(cs, files, CancellationToken.None));
        Assert.Equal(Real.Count, results.Sum(r => r.Count));
        Assert.Equal(1L, await Scalar<long>(cs, "select count(*) from schema_migrations where version = '0001'"));
    }

    [Fact]
    public async Task DB_02_schema_migrations_readable_by_app_and_reader()
    {
        await using var db = await fx.CreateDatabaseAsync();
        Assert.Equal((long)Real.Count, await Scalar<long>(db.AppConnectionString, "select count(*) from schema_migrations"));
        Assert.Equal((long)Real.Count, await Scalar<long>(db.ReaderConnectionString, "select count(*) from schema_migrations"));
    }

    [Fact]
    public async Task DB_02_migrator_command_fails_on_missing_url_without_leaking()
    {
        var output = new StringWriter();
        Assert.NotEqual(0, await MigratorCommand.RunAsync(null, Real, output, CancellationToken.None));
        Assert.NotEqual(0, await MigratorCommand.RunAsync("", Real, output, CancellationToken.None));
        Assert.NotEqual(0, await MigratorCommand.RunAsync("mysql://u:hunter2@h/x", Real, output, CancellationToken.None));
        Assert.DoesNotContain("hunter2", output.ToString());
    }

    [Fact]
    public async Task DB_02_migrator_command_prints_applied_versions()
    {
        await using var db = await fx.CreateUnmigratedDatabaseAsync();
        var output = new StringWriter();
        var code = await MigratorCommand.RunAsync(fx.MigratorUrl(db.Name), Real, output, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("0001", output.ToString());
        Assert.DoesNotContain("olive_migrator:", output.ToString());
    }

    [Fact]
    public async Task DB_02_fixture_role_assertion_rejects_superuser()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => PostgresFixture.AssertLeastPrivilegeAsync(fx.AdminConnectionString("postgres")));
    }

    [Fact]
    public void DB_02_only_migrator_reads_migrator_url()
    {
        var allowed = new[] { "Olive.Migrator/Program.cs", "Olive.Host/Startup/StartupGuards.cs" };
        var offenders = Directory.EnumerateFiles(Path.Combine(TestPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => File.ReadAllText(p).Contains("OLIVE_MIGRATOR_URL"))
            .Select(p => p.Replace('\\', '/'))
            .Where(p => !allowed.Any(a => p.EndsWith(a, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(offenders);
    }
}
