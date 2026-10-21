using System.Text.RegularExpressions;
using Npgsql;

namespace Olive.Tests;

internal static class AblationDatabase
{
    // RLS ablation needs ownership; olive_app cannot do this (Pitfall 2).
    public static async Task<TestDatabase> CreateAsync(PostgresFixture fixture, params string[] tablesWithRlsDisabled)
    {
        var db = await fixture.CreateDatabaseAsync();
        if (tablesWithRlsDisabled.Length == 0)
        {
            return db;
        }

        await using var conn = new NpgsqlConnection(fixture.AdminConnectionString(db.Name));
        await conn.OpenAsync();
        foreach (var table in tablesWithRlsDisabled)
        {
            if (!Regex.IsMatch(table, "^[a-z_]+$"))
            {
                throw new ArgumentException("Invalid table name: " + table, nameof(tablesWithRlsDisabled));
            }

            await using var cmd = new NpgsqlCommand($"set role olive_owner; alter table {table} disable row level security", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        return db;
    }
}
