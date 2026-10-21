using Npgsql;

namespace Olive.Persistence;

// DB-07: startup checks on the service's own database role and on the applied migration set.
// Returns fixed violation codes only; never any configured or database-supplied data.
public static class DatabaseSafetyChecks
{
    public static async Task<IReadOnlyList<string>> CheckAsync(NpgsqlDataSource dataSource, IReadOnlyList<MigrationFile> expected, CancellationToken ct)
    {
        var violations = new List<string>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "select r.rolsuper, r.rolbypassrls, pg_has_role(current_user, 'olive_owner', 'MEMBER'), " +
            "exists (select 1 from pg_class c join pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname = 'public' and c.relowner = r.oid) " +
            "from pg_roles r where r.rolname = current_user", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                violations.Add("role_unknown");
            }
            else
            {
                if (reader.GetBoolean(0)) { violations.Add("role_superuser"); }
                if (reader.GetBoolean(1)) { violations.Add("role_bypassrls"); }
                if (reader.GetBoolean(2)) { violations.Add("role_owner_member"); }
                if (reader.GetBoolean(3)) { violations.Add("role_owns_objects"); }
            }
        }

        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await using var cmd = new NpgsqlCommand("select version, sha256 from schema_migrations", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                recorded[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (PostgresException e) when (e.SqlState == "42P01")
        {
            // No schema_migrations table: nothing applied.
        }

        var known = expected.ToDictionary(f => f.Version, f => f.Sha256, StringComparer.Ordinal);
        if (known.Keys.Any(v => !recorded.ContainsKey(v))) { violations.Add("migrations_behind"); }
        if (recorded.Keys.Any(v => !known.ContainsKey(v))) { violations.Add("migrations_ahead"); }
        if (known.Any(k => recorded.TryGetValue(k.Key, out var sha) && !string.Equals(sha, k.Value, StringComparison.Ordinal)))
        {
            violations.Add("migrations_checksum");
        }

        return violations;
    }
}
