using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Olive.Contracts;

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

    internal static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage r, HttpStatusCode status, string reason, params string[] forbidden)
    {
        Assert.Equal(status, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        var text = await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(reason, body.GetProperty("reason").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("StackTrace", text);
        foreach (var f in forbidden) { Assert.DoesNotContain(f, text); }
        return body;
    }

    public static TheoryData<string> InvalidBodies() => new()
    {
        "not json",
        "[]",
        "\"run-1\"",
        "{}",
        "{\"run_id\":1}",
        "{\"run_id\":\"\"}",
        "{\"run_id\":null}",
        "{\"run_id\":\"r\",\"parameters\":[]}",
        "{\"run_id\":\"r\",\"parameters\":null}",
        "{\"run_id\":\"r\",\"action_type\":5}",
        "{\"run_id\":\"r\",\"nono_session_id\":{}}",
        "{\"run_id\":\"r\",\"party_ref\":null}",
        "{\"run_id\":\"r\",\"parameters\":{\"a\":\"\\u0000\"}}",
        "{\"run_id\":\"r\",\"parameters\":{\"d\":" + new string('[', 70) + new string(']', 70) + "}}",
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task API_01_invalid_bodies_give_400_and_store_nothing(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
        await AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.InvalidRequest);
        Assert.Equal(0, await CountRowsAsync(db, Alpha));
        Assert.DoesNotContain("insert", factory.Events);
    }

    public static TheoryData<string> LoneSurrogateBodies() => new()
    {
        "{\"run_id\":\"\\ud800\"}",
        "{\"run_id\":\"r\",\"justification\":\"\\udc00\"}",
        "{\"run_id\":\"r\",\"parameters\":{\"x\":\"\\ud800\"}}",
        "{\"run_id\":\"r\",\"parameters\":{\"\\ud800\":\"x\"}}",
        "{\"run_id\":\"r\",\"parameters\":{\"a\":[{\"\\ud800\":1}]}}",
        "{\"run_id\":\"r\",\"parameters\":{\"\\u0000\":1}}",
    };

    [Theory]
    [MemberData(nameof(LoneSurrogateBodies))]
    public async Task API_01_lone_surrogate_and_nul_escapes_give_400_and_store_nothing(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
        await AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.InvalidRequest);
        Assert.Equal(0, await CountRowsAsync(db, Alpha));
        Assert.DoesNotContain("insert", factory.Events);
        Assert.False(factory.Logs.HasErrorOrAbove);
    }

    [Fact]
    public async Task API_01_non_json_content_type_gives_400()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", new StringContent("{\"run_id\":\"r\"}", Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);
        await AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.InvalidRequest);
        Assert.Equal(0, await CountRowsAsync(db, Alpha));
    }

    [Fact]
    public async Task API_01_only_run_id_is_mandatory()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json("{\"run_id\":\"only\",\"unknown\":1}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(1, await CountRowsAsync(db, Alpha));
    }

    private static string BodyOfSize(int total)
    {
        const string prefix = "{\"run_id\":\"r\",\"justification\":\"";
        const string suffix = "\"}";
        return prefix + new string('a', total - prefix.Length - suffix.Length) + suffix;
    }

    [Fact]
    public async Task API_01_oversized_body_gives_413()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var body = BodyOfSize(1_048_577);
        Assert.Equal(1_048_577, Encoding.UTF8.GetByteCount(body));
        var r = await alpha.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
        await AssertProblemAsync(r, HttpStatusCode.RequestEntityTooLarge, ProblemReasons.PayloadTooLarge);
        Assert.Equal(0, await CountRowsAsync(db, Alpha));
    }

    [Fact]
    public async Task API_01_body_at_the_limit_is_accepted()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(BodyOfSize(1_048_576)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task API_01_duplicate_run_gives_409_and_other_tenant_may_reuse_run_id()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        using var beta = factory.CreateKeyClient(BetaKey);
        Assert.Equal(HttpStatusCode.Created, (await alpha.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken)).StatusCode);
        var dup = await alpha.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken);
        await AssertProblemAsync(dup, HttpStatusCode.Conflict, ProblemReasons.DuplicateRun);
        Assert.Equal(1, await CountRowsAsync(db, Alpha));
        Assert.Equal(HttpStatusCode.Created, (await beta.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(1, await CountRowsAsync(db, Beta));
    }

    [Fact]
    public async Task API_02_cross_tenant_and_missing_ids_give_identical_404()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        using var beta = factory.CreateKeyClient(BetaKey);
        var post = await beta.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken);
        var betaId = JsonDocument.Parse(await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("proposal_id").GetGuid();

        var cross = await alpha.GetAsync($"/v1/proposals/{betaId}", TestContext.Current.CancellationToken);
        var missing = await alpha.GetAsync($"/v1/proposals/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var a = await AssertProblemAsync(cross, HttpStatusCode.NotFound, ProblemReasons.ProposalNotFound);
        var b = await AssertProblemAsync(missing, HttpStatusCode.NotFound, ProblemReasons.ProposalNotFound);
        Assert.Equal(a.GetProperty("type").GetString(), b.GetProperty("type").GetString());
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
    }

    [Fact]
    public async Task API_02_malformed_id_is_not_found_not_server_error()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.GetAsync("/v1/proposals/not-a-guid", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task API_01_error_responses_contain_no_session_key()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var texts = new List<string>();
        foreach (var body in new[] { "not json", "{}", FullBody, FullBody })
        {
            var r = await alpha.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
            texts.Add(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using var bad = factory.CreateKeyClient("fixture-key-inactive");
        var rb = await bad.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken);
        texts.Add(await rb.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach (var t in texts)
        {
            Assert.DoesNotContain(AlphaKey, t);
            Assert.DoesNotContain("fixture-key-inactive", t);
        }

        Assert.DoesNotContain(AlphaKey, factory.Logs.Text);
        Assert.DoesNotContain("fixture-key-inactive", factory.Logs.Text);
    }
}
