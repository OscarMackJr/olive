using Npgsql;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class DatabaseSafetyTests(PostgresFixture fixture)
{
    private static readonly IReadOnlyList<MigrationFile> Expected = MigrationCatalog.LoadEmbedded();

    private static async Task<IReadOnlyList<string>> CheckAsync(string connectionString)
    {
        await using var ds = NpgsqlDataSource.Create(connectionString);
        return await DatabaseSafetyChecks.CheckAsync(ds, Expected, TestContext.Current.CancellationToken);
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private string RoleConnection(string role, string password, string database) =>
        new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString(database)) { Username = role, Password = password, Pooling = false }.ConnectionString;

    [Fact]
    public async Task DB_07_superuser_is_flagged()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Assert.Contains("role_superuser", await CheckAsync(fixture.AdminConnectionString(db.Name)));
    }

    [Fact]
    public async Task DB_07_owner_member_is_flagged()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Assert.Contains("role_owner_member", await CheckAsync(fixture.MigratorConnectionString(db.Name)));
    }

    [Fact]
    public async Task DB_07_bypassrls_role_is_flagged_and_cleaned_up()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var role = "olive_probe_bypass_" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.AdminConnectionString("postgres");
        await ExecAsync(admin, $"create role {role} login bypassrls password 'probe-pw'");
        try
        {
            await ExecAsync(admin, $"grant connect on database {db.Name} to {role}");
            Assert.Contains("role_bypassrls", await CheckAsync(RoleConnection(role, "probe-pw", db.Name)));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecAsync(admin, $"drop owned by {role}; drop role {role}");
        }

        Assert.Equal(0, await ScalarAsync(admin, $"select count(*) from pg_roles where rolname = '{role}'"));
    }

    [Fact]
    public async Task DB_07_role_owning_public_objects_is_flagged_and_cleaned_up()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var role = "olive_probe_owner_" + Guid.NewGuid().ToString("N")[..8];
        var admin = fixture.AdminConnectionString("postgres");
        var adminDb = fixture.AdminConnectionString(db.Name);
        await ExecAsync(admin, $"create role {role} login password 'probe-pw'");
        try
        {
            await ExecAsync(admin, $"grant connect on database {db.Name} to {role}");
            await ExecAsync(adminDb, $"create table public.probe_owned (id int); alter table public.probe_owned owner to {role}");
            Assert.Contains("role_owns_objects", await CheckAsync(RoleConnection(role, "probe-pw", db.Name)));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecAsync(adminDb, $"drop owned by {role}");
            await ExecAsync(admin, $"drop role {role}");
        }

        Assert.Equal(0, await ScalarAsync(admin, "select count(*) from pg_roles where rolname like 'olive_%probe%'"));
    }

    [Theory]
    [InlineData("olive_app")]
    [InlineData("olive_reader")]
    public async Task DB_07_least_privilege_roles_on_migrated_database_are_safe(string role)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Assert.Empty(await CheckAsync(fixture.RoleConnectionString(role, db.Name)));
    }

    [Fact]
    public async Task DB_07_missing_migration_row_is_behind()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await ExecAsync(fixture.AdminConnectionString(db.Name), $"delete from schema_migrations where version = '{Expected[0].Version}'");
        Assert.Contains("migrations_behind", await CheckAsync(db.AppConnectionString));
    }

    [Fact]
    public async Task DB_07_changed_checksum_is_flagged()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await ExecAsync(fixture.AdminConnectionString(db.Name), $"update schema_migrations set sha256 = 'deadbeef' where version = '{Expected[0].Version}'");
        Assert.Contains("migrations_checksum", await CheckAsync(db.AppConnectionString));
    }

    [Fact]
    public async Task DB_07_extra_migration_row_is_ahead()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await ExecAsync(fixture.AdminConnectionString(db.Name), "insert into schema_migrations (version, filename, sha256) values ('9999', '9999_future.sql', 'x')");
        Assert.Contains("migrations_ahead", await CheckAsync(db.AppConnectionString));
    }

    [Fact]
    public async Task DB_07_unmigrated_database_is_behind_and_does_not_throw()
    {
        await using var db = await fixture.CreateUnmigratedDatabaseAsync();
        Assert.Contains("migrations_behind", await CheckAsync(db.AppConnectionString));
    }

    [Fact]
    public async Task DB_07_host_refuses_superuser_without_leaking_connection_details()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var b = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString(db.Name));
        var url = $"postgresql://{b.Username}:{b.Password}@{b.Host}:{b.Port}/{b.Database}";
        await using var factory = new OliveApiFactory(db, replaceIntrospector: false,
            extraConfig: new Dictionary<string, string?> { ["OLIVE_POSTGRES_URL"] = url });
        var guard = await StartupGuardTests.TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("DB-07", guard.Message);
        Assert.Contains("role_superuser", guard.Message);
        Assert.DoesNotContain("postgresql://", guard.Message);
        Assert.DoesNotContain("Password", guard.Message);
        Assert.DoesNotContain(b.Password!, guard.Message);
        Assert.DoesNotContain(b.Host!, guard.Message);
    }

    [Fact]
    public async Task DB_07_host_refuses_missing_database_url()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, replaceIntrospector: false,
            extraConfig: new Dictionary<string, string?> { ["OLIVE_POSTGRES_URL"] = null });
        var guard = await StartupGuardTests.TryStartAsync(factory);
        Assert.Equal("DB-07", guard?.Rule);
    }

    [Fact]
    public async Task DB_07_host_refuses_unmigrated_database()
    {
        await using var db = await fixture.CreateUnmigratedDatabaseAsync();
        await using var factory = new OliveApiFactory(db, replaceIntrospector: false);
        var guard = await StartupGuardTests.TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("migrations_behind", guard.Message);
    }

    [Fact]
    public async Task DB_07_host_starts_with_olive_app_on_migrated_database()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, replaceIntrospector: false);
        Assert.Null(await StartupGuardTests.TryStartAsync(factory));
    }

    [Fact]
    public async Task DB_07_no_probe_roles_remain()
    {
        await Task.Delay(0, TestContext.Current.CancellationToken);
        // Probe roles of concurrently running tests in this class are excluded by sequential execution within a class.
        Assert.Equal(0, await ScalarAsync(fixture.AdminConnectionString("postgres"), "select count(*) from pg_roles where rolname like 'olive_%probe%'"));
    }
}
