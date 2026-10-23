using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Olive.Application;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ProposalStoreHashTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Body = "{\"a\":1,\"b\":[true]}";

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static NewProposal Sample(string agent = "agent-1", string run = "run-1", bool hashes = true) => new(
        agent, "key-1", "user-1", "case_management", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
        "nono-1", run, "crm.case_note@1", "party://p1", Body, "because", "trace://t1",
        hashes ? Body : null, hashes ? Sha(Body) : null, hashes ? Sha("four-key") : null);

    private static PostgresProposalStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)));

    private static async Task<(string Body, string BodySha, string Hash)> ReadHashesAsync(TestDatabase db, Guid id)
    {
        await using var ds = NpgsqlDataSource.Create(db.ReaderConnectionString);
        return await new TenantUnitOfWork(ds).ExecuteAsync(TestPaths.TenantAlpha, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand("select canonical_body, body_sha256, proposal_hash from proposal where proposal_id = @p", conn, tx);
            cmd.Parameters.AddWithValue("p", id);
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            await r.ReadAsync(Ct);
            return (r.GetString(0), r.GetString(1), r.GetString(2));
        }, Ct);
    }

    [Fact]
    public async Task API_06_insert_persists_hashes_and_stored_bytes_recompute_to_stored_sha()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var id = await Store(db).InsertAsync(TestPaths.TenantAlpha, Sample(), Ct);
        Assert.NotNull(id);
        var (body, bodySha, hash) = await ReadHashesAsync(db, id!.Value);
        Assert.Equal(Body, body);
        Assert.Equal(Sha(Body), bodySha);
        Assert.Equal(Sha("four-key"), hash);
        Assert.Equal(Sha(body), bodySha);
    }

    [Fact]
    public async Task API_06_find_by_run_returns_id_state_and_hash_and_is_tenant_scoped()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct);
        var found = await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "run-1", Ct);
        Assert.NotNull(found);
        Assert.Equal(id, found!.ProposalId);
        Assert.Equal("proposed", found.State);
        Assert.Equal(Sha(Body), found.BodySha256);
        Assert.Equal("user-1", found.OnBehalfOf);
        Assert.Equal("case_management", found.Purpose);
        Assert.Null(await store.FindByRunAsync(TestPaths.TenantBeta, "agent-1", "run-1", Ct));
        Assert.Null(await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "run-other", Ct));
    }

    [Fact]
    public async Task API_06_find_by_run_returns_current_state_after_transition()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct);
        Assert.True(await store.TransitionAsync(TestPaths.TenantAlpha, id!.Value, "proposed", "awaiting_approval", "reviewer", Ct));
        Assert.Equal("awaiting_approval", (await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "run-1", Ct))!.State);
    }

    [Fact]
    public async Task API_06_legacy_row_without_hashes_surfaces_null_sha()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        await store.InsertAsync(TestPaths.TenantAlpha, Sample(run: "legacy", hashes: false), Ct);
        var found = await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "legacy", Ct);
        Assert.NotNull(found);
        Assert.Null(found!.BodySha256);
    }

    [Fact]
    public async Task API_06_duplicate_run_insert_still_returns_null()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        Assert.NotNull(await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct));
        Assert.Null(await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct));
    }
}
