using Npgsql;

namespace Olive.Persistence;

public sealed class MigrationRunner
{
    // "OLIV" in ASCII; advisory lock key serialising concurrent migrator runs.
    private const long LockKey = 0x4F4C4956;

    public async Task<IReadOnlyList<string>> RunAsync(string migratorConnectionString, IReadOnlyList<MigrationFile> files, CancellationToken ct)
    {
        var applied = new List<string>();
        await using var conn = new NpgsqlConnection(migratorConnectionString);
        await conn.OpenAsync(ct);
        await Exec(conn, "select pg_advisory_lock(" + LockKey + ")", ct);
        try
        {
            await Exec(conn, "set role olive_owner", ct);
            await Exec(conn, "create table if not exists schema_migrations (version text primary key, filename text not null, sha256 text not null, applied_at timestamptz not null default now())", ct);
            await Exec(conn, "grant select on schema_migrations to olive_app, olive_reader", ct);

            var recorded = new Dictionary<string, string>();
            await using (var cmd = new NpgsqlCommand("select version, sha256 from schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    recorded[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var known = files.Select(f => f.Version).ToHashSet();
            foreach (var version in recorded.Keys.Order(StringComparer.Ordinal))
            {
                if (!known.Contains(version))
                {
                    throw new MigrationDriftException(version);
                }
            }

            foreach (var file in files)
            {
                if (recorded.TryGetValue(file.Version, out var sha))
                {
                    if (!string.Equals(sha, file.Sha256, StringComparison.Ordinal))
                    {
                        throw new MigrationChecksumException(file.Version);
                    }

                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var run = new NpgsqlCommand(file.Sql, conn, tx))
                {
                    await run.ExecuteNonQueryAsync(ct);
                }

                await using (var ins = new NpgsqlCommand("insert into schema_migrations (version, filename, sha256) values (@v, @f, @s)", conn, tx))
                {
                    ins.Parameters.AddWithValue("v", file.Version);
                    ins.Parameters.AddWithValue("f", file.FileName);
                    ins.Parameters.AddWithValue("s", file.Sha256);
                    await ins.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                applied.Add(file.Version);
            }
        }
        finally
        {
            if (conn.State == System.Data.ConnectionState.Open)
            {
                await Exec(conn, "select pg_advisory_unlock(" + LockKey + ")", CancellationToken.None);
            }
        }

        return applied;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
