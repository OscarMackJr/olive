using Npgsql;

namespace Olive.Tests;

internal static class NoSharedPostgres
{
    // D-16: tests must never touch the shared dev Postgres on localhost:5432.
    public static void Assert(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var local = b.Host is "localhost" or "127.0.0.1" or "::1";
        if (local && b.Port == 5432)
        {
            throw new InvalidOperationException("D-16: refusing to connect to localhost:5432.");
        }
    }
}

public sealed class TestDatabase(PostgresFixture fixture, string name) : IAsyncDisposable
{
    public string Name { get; } = name;

    public string AppConnectionString => fixture.RoleConnectionString("olive_app", Name);

    public string ReaderConnectionString => fixture.RoleConnectionString("olive_reader", Name);

    public string AppUrl => fixture.RoleUrl("olive_app", Name);

    public string ReaderUrl => fixture.RoleUrl("olive_reader", Name);

    public static string WithMaxPoolSize(string connectionString, int size) =>
        new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = size }.ConnectionString;

    public async Task<Guid> SeedProposalAsync(Guid tenantId, string agentId, string runId)
    {
        var proposalId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using (var ins = new NpgsqlCommand(
            "insert into proposal (tenant_id, proposal_id, agent_id, key_id, on_behalf_of, purpose, key_expires_at, run_id) " +
            "values (@t, @p, @a, 'key-1', 'user-1', 'case_management', now() + interval '1 hour', @r)", conn, tx))
        {
            ins.Parameters.AddWithValue("t", tenantId);
            ins.Parameters.AddWithValue("p", proposalId);
            ins.Parameters.AddWithValue("a", agentId);
            ins.Parameters.AddWithValue("r", runId);
            await ins.ExecuteNonQueryAsync();
        }

        await using (var ev = new NpgsqlCommand(
            "insert into proposal_event (tenant_id, event_id, proposal_id, event_type, to_state, actor) " +
            "values (@t, @e, @p, 'submitted', 'proposed', @a)", conn, tx))
        {
            ev.Parameters.AddWithValue("t", tenantId);
            ev.Parameters.AddWithValue("e", Guid.NewGuid());
            ev.Parameters.AddWithValue("p", proposalId);
            ev.Parameters.AddWithValue("a", agentId);
            await ev.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return proposalId;
    }

    // Inserts an open action_approval queue_item (plus a created event) for an existing proposal, as olive_app.
    public async Task<Guid> SeedQueueItemAsync(Guid tenantId, Guid proposalId)
    {
        var itemId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using (var ins = new NpgsqlCommand(
            "insert into queue_item (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, proposal_id) " +
            "values (@t, @i, 'action_approval', 'proposal', 'olive', @p, 'olive:proposal/' || @p::text, 'olive:proposal/' || @p::text, @p)", conn, tx))
        {
            ins.Parameters.AddWithValue("t", tenantId);
            ins.Parameters.AddWithValue("i", itemId);
            ins.Parameters.AddWithValue("p", proposalId);
            await ins.ExecuteNonQueryAsync();
        }

        await using (var ev = new NpgsqlCommand(
            "insert into queue_item_event (tenant_id, event_id, item_id, event_type, to_state, actor) " +
            "values (@t, @e, @i, 'created', 'open', 'olive')", conn, tx))
        {
            ev.Parameters.AddWithValue("t", tenantId);
            ev.Parameters.AddWithValue("e", Guid.NewGuid());
            ev.Parameters.AddWithValue("i", itemId);
            await ev.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return itemId;
    }

    // Test-only: runs SQL as the container superuser (bypasses RLS and column grants), e.g. to backdate created_at.
    public async Task<int> AdminExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.AdminConnectionString(Name));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await fixture.DropDatabaseAsync(Name);
    }
}
