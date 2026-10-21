using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ApiProposalTests(PostgresFixture fixture)
{
    internal const string AlphaKey = "fixture-key-liam-alpha";
    internal const string BetaKey = "fixture-key-liam-beta";
    internal static readonly Guid Alpha = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");
    internal static readonly Guid Beta = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");

    internal const string FullBody =
        "{\"run_id\":\"run-1\",\"action_type\":\"crm.case_note@1\",\"party_ref\":\"party://p1\",\"parameters\":{\"case_ref\":\"payload://c1\",\"note_ref\":\"payload://n1\"},\"justification\":\"j\",\"trace_ref\":\"trace://t1\",\"nono_session_id\":\"nono-1\"}";

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    internal static async Task<NpgsqlConnection> OpenReaderAsync(TestDatabase db, Guid tenant, NpgsqlTransaction? _ = null)
    {
        var conn = new NpgsqlConnection(db.ReaderConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    // Runs a scalar query as olive_reader inside a transaction with the tenant set.
    internal static async Task<T?> ReaderScalarAsync<T>(TestDatabase db, Guid tenant, string sql, params (string, object)[] args)
    {
        await using var conn = await OpenReaderAsync(db, tenant);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", tenant.ToString());
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (n, v) in args) { cmd.Parameters.AddWithValue(n, v); }
        var o = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return o is null or DBNull ? default : (T)o;
    }

    internal static async Task<long> CountRowsAsync(TestDatabase db, Guid tenant) =>
        await ReaderScalarAsync<long>(db, tenant, "select count(*) from proposal");

    [Fact]
    public async Task API_01_post_then_get_roundtrip_through_real_postgres()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);

        var post = await alpha.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = JsonDocument.Parse(await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var props = created.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["proposal_id", "state"], props);
        var id = created.GetProperty("proposal_id").GetGuid();
        Assert.Equal("proposed", created.GetProperty("state").GetString());
        Assert.Equal($"/v1/proposals/{id}", post.Headers.Location!.OriginalString);

        // The row is really in Postgres, read back as olive_reader under RLS.
        Assert.Equal("proposed", await ReaderScalarAsync<string>(db, Alpha, "select state from proposal where proposal_id = @p", ("p", id)));

        var get = await alpha.GetAsync($"/v1/proposals/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(id, body.GetProperty("proposal_id").GetGuid());
        Assert.Equal("proposed", body.GetProperty("state").GetString());
        Assert.Equal("run-1", body.GetProperty("run_id").GetString());
        Assert.Equal("liam", body.GetProperty("agent_id").GetString());
        Assert.Equal(Alpha, body.GetProperty("tenant_id").GetGuid());
        Assert.Equal("payload://c1", body.GetProperty("parameters").GetProperty("case_ref").GetString());
        Assert.Equal("payload://n1", body.GetProperty("parameters").GetProperty("note_ref").GetString());

        using var beta = factory.CreateKeyClient(BetaKey);
        var cross = await beta.GetAsync($"/v1/proposals/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
    }
}
