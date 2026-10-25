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

    // Inserts an identity_review queue_item in any state shape (container superuser, bypasses RLS and grants) and returns the item_id.
    public async Task<Guid> SeedIdentityItemAsync(
        Guid tenantId,
        Guid producerRef,
        string state = "open",
        string? claimedBy = null,
        string? decision = null,
        string? decidedBy = null,
        DateTimeOffset? decidedAt = null,
        string? withdrawnReason = null,
        string producer = "bluto")
    {
        var itemId = Guid.NewGuid();
        var bodySha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(producerRef.ToByteArray()));
        var claimed = state is "claimed" or "decided";
        var decided = state == "decided";
        await using var conn = new NpgsqlConnection(fixture.AdminConnectionString(Name));
        await conn.OpenAsync();
        await using var ins = new NpgsqlCommand(
            "insert into queue_item (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, ruleset_version, body_sha256, " +
            "state, claimed_by, claimed_at, decision, decided_by, decided_at, rationale, auth_source, withdrawn_reason) " +
            "values (@t, @i, 'identity_review', 'candidate_pair', @pr, @ref, 'bluto:candidate/' || @ref::text, 'bluto:review-evidence/' || @ref::text, 'test-ruleset', @h, " +
            "@s, @cb, @ca, @d, @db, @da, @r, @au, @w)", conn);
        ins.Parameters.AddWithValue("t", tenantId);
        ins.Parameters.AddWithValue("i", itemId);
        ins.Parameters.AddWithValue("pr", producer);
        ins.Parameters.AddWithValue("ref", producerRef);
        ins.Parameters.AddWithValue("h", bodySha);
        ins.Parameters.AddWithValue("s", state);
        ins.Parameters.AddWithValue("cb", (object?)(claimed ? claimedBy ?? "seed-reviewer" : claimedBy) ?? DBNull.Value);
        ins.Parameters.AddWithValue("ca", claimed ? DateTimeOffset.UtcNow : DBNull.Value);
        ins.Parameters.AddWithValue("d", (object?)decision ?? DBNull.Value);
        ins.Parameters.AddWithValue("db", (object?)(decided ? decidedBy ?? claimedBy ?? "seed-reviewer" : decidedBy) ?? DBNull.Value);
        ins.Parameters.AddWithValue("da", decided ? decidedAt ?? DateTimeOffset.UtcNow : (object?)decidedAt ?? DBNull.Value);
        ins.Parameters.AddWithValue("r", decided ? "seeded rationale" : DBNull.Value);
        ins.Parameters.AddWithValue("au", decided ? "stub" : DBNull.Value);
        ins.Parameters.AddWithValue("w", (object?)withdrawnReason ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync();
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
