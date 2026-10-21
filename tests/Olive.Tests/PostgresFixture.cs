using Npgsql;
using Olive.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Olive.Tests.PostgresFixture))]

namespace Olive.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    // D-17: major version of deploy-postgres-1 (17.10, postgres:17-alpine).
    public const string PostgresImage = "postgres:17-alpine";

    // Test-only credentials for a throwaway container.
    private const string MigratorPassword = "test-migrator";
    private const string AppPassword = "test-app";
    private const string ReaderPassword = "test-reader";
    private const string TemplateName = "olive_template";

    private PostgreSqlContainer _container = null!;
    private string _host = "";
    private int _port;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder(PostgresImage).Build();
        await _container.StartAsync();
        _host = _container.Hostname;
        _port = _container.GetMappedPublicPort(5432);
        NoSharedPostgres.Assert(Build("postgres", "postgres", "postgres", pooling: false));

        var roles = await File.ReadAllTextAsync(Path.Combine(TestPaths.Root, "sql", "bootstrap", "001_roles.sql"));
        await AdminExec("postgres", roles);
        await AdminExec("postgres",
            $"alter role olive_migrator password '{MigratorPassword}'; " +
            $"alter role olive_app password '{AppPassword}'; " +
            $"alter role olive_reader password '{ReaderPassword}'");
        await CreateOwnedDatabase(TemplateName);

        var files = MigrationCatalog.LoadEmbedded();
        await new MigrationRunner().RunAsync(MigratorConnectionString(TemplateName), files, CancellationToken.None);

        await AssertLeastPrivilegeAsync(RoleConnectionString("olive_app", TemplateName));
        await AssertLeastPrivilegeAsync(RoleConnectionString("olive_reader", TemplateName));
        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    // Throws unless the connected role is neither superuser, BYPASSRLS nor a member of olive_owner.
    public static async Task AssertLeastPrivilegeAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select r.rolsuper, r.rolbypassrls, pg_has_role(current_user, 'olive_owner', 'MEMBER') " +
            "from pg_roles r where r.rolname = current_user", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2))
        {
            throw new InvalidOperationException("Test role is privileged: RLS tests would pass vacuously.");
        }
    }

    public Task<TestDatabase> CreateDatabaseAsync() => CloneAsync(migrated: true);

    public Task<TestDatabase> CreateUnmigratedDatabaseAsync() => CloneAsync(migrated: false);

    private async Task<TestDatabase> CloneAsync(bool migrated)
    {
        var name = "olive_t_" + Guid.NewGuid().ToString("N");
        if (migrated)
        {
            await AdminExec("postgres", $"create database {name} template {TemplateName} owner olive_owner");
            await GrantConnect(name);
        }
        else
        {
            await CreateOwnedDatabase(name);
        }

        return new TestDatabase(this, name);
    }

    private async Task CreateOwnedDatabase(string name)
    {
        await AdminExec("postgres", $"create database {name} owner olive_owner");
        await GrantConnect(name);
    }

    private Task GrantConnect(string name) => AdminExec("postgres",
        $"revoke all on database {name} from public; grant connect on database {name} to olive_migrator, olive_app, olive_reader");

    internal Task DropDatabaseAsync(string name) => AdminExec("postgres", $"drop database if exists {name} with (force)");

    private async Task AdminExec(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // For DB_01/DB_02 tests only; RLS tests must use TestDatabase app/reader strings.
    internal string AdminConnectionString(string database) => Build("postgres", "postgres", database, pooling: false);

    // For DB_02 tests only.
    internal string MigratorConnectionString(string database) => Build("olive_migrator", MigratorPassword, database, pooling: false);

    internal string MigratorUrl(string database) => $"postgresql://olive_migrator:{MigratorPassword}@{_host}:{_port}/{database}";

    internal string RoleConnectionString(string role, string database)
    {
        var password = role == "olive_app" ? AppPassword : ReaderPassword;
        return Build(role, password, database, pooling: true);
    }

    internal string RoleUrl(string role, string database)
    {
        var password = role == "olive_app" ? AppPassword : ReaderPassword;
        return $"postgresql://{role}:{password}@{_host}:{_port}/{database}";
    }

    private string Build(string user, string password, string database, bool pooling)
    {
        var b = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Host = _host,
            Port = _port,
            Username = user,
            Password = password,
            Database = database,
            Pooling = pooling,
        };
        var cs = b.ConnectionString;
        NoSharedPostgres.Assert(cs);
        return cs;
    }
}
