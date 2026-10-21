namespace Olive.Persistence;

public static class MigratorCommand
{
    public static async Task<int> RunAsync(string? migratorUrl, IReadOnlyList<MigrationFile> files, TextWriter output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(migratorUrl))
        {
            await output.WriteLineAsync("Migrator connection setting is missing.");
            return 2;
        }

        try
        {
            var connectionString = PostgresUrl.ToNpgsql(migratorUrl);
            var applied = await new MigrationRunner().RunAsync(connectionString, files, ct);
            await output.WriteLineAsync(applied.Count == 0
                ? "No migrations to apply."
                : "Applied migrations: " + string.Join(", ", applied));
            return 0;
        }
        catch (FormatException)
        {
            await output.WriteLineAsync("Migrator connection setting is invalid.");
            return 2;
        }
        catch (MigrationChecksumException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 3;
        }
        catch (MigrationDriftException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 3;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            await output.WriteLineAsync("Migration failed: " + ex.GetType().Name);
            return 1;
        }
    }
}
