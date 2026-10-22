using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Olive.Application;
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

    // A valid all-required envelope (API-04). Optional nono_session_id is omitted unless given.
    internal static string BodyFor(string runId, string? nonoSessionId = null, string? partyRef = null) =>
        "{\"run_id\":\"" + runId + "\",\"action_type\":\"crm.case_note@1\",\"party_ref\":\"" + (partyRef ?? "party://p1")
        + "\",\"parameters\":{\"case_ref\":\"payload://c1\",\"note_ref\":\"payload://n1\"},\"justification\":\"j\",\"trace_ref\":\"trace://t1\""
        + (nonoSessionId is null ? "" : ",\"nono_session_id\":\"" + nonoSessionId + "\"") + "}";

    // A catalog whose actions accept any parameters object, so hostile-but-gate-legal parameter content can reach storage.
    internal static Olive.Catalog.CatalogSet PermissiveCatalog()
    {
        var any = Olive.Catalog.SchemaValidator.Compile("{\"type\":\"object\"}");
        return new([new Olive.Catalog.CatalogRelease(1,
        [
            new Olive.Catalog.CatalogEntry("test.open@1", JsonDocument.Parse("{}").RootElement, any),
            new Olive.Catalog.CatalogEntry("crm.case_note@1", JsonDocument.Parse("{}").RootElement, any),
        ])]);
    }

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
        "{\"run_id\":\"r\",\"parameters\":{\"a\":\"\\u0000\"}}",
        """{"run_id":"r","parameters":{"\u0000":1}}""",
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

    public static TheoryData<string> SchemaViolationBodies() => new()
    {
        "{\"run_id\":\"r\",\"parameters\":[]}",
        "{\"run_id\":\"r\",\"parameters\":null}",
        "{\"run_id\":\"r\",\"action_type\":5}",
        "{\"run_id\":\"r\",\"nono_session_id\":{}}",
        "{\"run_id\":\"r\",\"party_ref\":null}",
        "{\"run_id\":\"only\",\"unknown\":1}",
    };

    [Theory]
    [MemberData(nameof(SchemaViolationBodies))]
    public async Task API_05_envelope_schema_violations_give_400_schema_violation_and_store_nothing(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
        await AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.SchemaViolation);
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

    // Hostile content stays in parameters, inside an otherwise-valid envelope for the permissive test.open@1 action.
    private static string NumBody(string parametersJson) =>
        "{\"run_id\":\"r\",\"action_type\":\"test.open@1\",\"party_ref\":\"party://p1\",\"parameters\":" + parametersJson
        + ",\"justification\":\"j\",\"trace_ref\":\"trace://t1\"}";

    public static TheoryData<string> NumberRangeBodies() => new()
    {
        NumBody("""{"x":1e131072}"""),
        NumBody("""{"x":1e-16384}"""),
        NumBody("""{"x":-1e999999}"""),
        NumBody("""{"x":[1,[1e131072]]}"""),
        NumBody("{\"x\":" + new string('9', 400) + "}"),
        NumBody("""{"x":1e131071}"""),
        NumBody("{\"x\":[" + string.Concat(Enumerable.Repeat("1e308,", 20000)) + "1]}"),
    };

    [Theory]
    [MemberData(nameof(NumberRangeBodies))]
    public async Task API_01_out_of_range_numbers_give_400_and_store_nothing(string body)
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

    public static TheoryData<string> ValidNumberAndStringBodies() => new()
    {
        NumBody("""{"x":0}"""),
        NumBody("""{"x":-1}"""),
        NumBody("""{"x":3.14}"""),
        NumBody("""{"x":1e10}"""),
        NumBody("""{"x":1.5e-7}"""),
        // D-05: a token that does not survive a double (12345678901234567890) is now refused; this one is exact.
        NumBody("""{"x":12345678901234567000}"""),
        NumBody("""{"x":1e300}"""),
        NumBody("""{"x":1e-300}"""),
        NumBody("{\"x\":[" + string.Join(",", Enumerable.Repeat("1e308", 100)) + "]}"),
        // WR-10: numbers without an exponent cannot expand, so they do not draw on the expansion budget.
        NumBody("{\"x\":[" + string.Join(",", Enumerable.Repeat("1", 70000)) + "]}"),
        NumBody("{\"x\":[" + string.Join(",", Enumerable.Repeat("1234567890", 7000)) + "]}"),
        NumBody("""{"x":"😀"}"""),
        NumBody("{}").Replace("\"justification\":\"j\"", "\"justification\":\"😀\""),
        NumBody("""{"x":"\\u0000"}"""),
    };

    [Theory]
    [MemberData(nameof(ValidNumberAndStringBodies))]
    public async Task API_01_valid_numbers_and_strings_still_accepted(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton(PermissiveCatalog()));
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(1, await CountRowsAsync(db, Alpha));
    }

    public static TheoryData<string> NulOutsideParametersBodies() => new()
    {
        """{"run_id":"a\u0000b"}""",
        """{"run_id":"r","action_type":"a\u0000b"}""",
        """{"run_id":"r","party_ref":"a\u0000b"}""",
        """{"run_id":"r","justification":"a\u0000b"}""",
        """{"run_id":"r","trace_ref":"a\u0000b"}""",
        """{"run_id":"r","nono_session_id":"a\u0000b"}""",
        NumBody("""{"x":["ok","a\u0000b"]}"""),
    };

    [Theory]
    [MemberData(nameof(NulOutsideParametersBodies))]
    public async Task API_01_nul_outside_parameters_gives_400_and_store_nothing(string body)
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

    private sealed class ThrowingStore(string sqlState) : IProposalStore
    {
        public Task<Guid?> InsertAsync(Guid tenantId, NewProposal proposal, CancellationToken ct) =>
            throw new PostgresException("secret-parameter-value", "ERROR", "ERROR", sqlState);

        public Task<ProposalRecord?> GetAsync(Guid tenantId, Guid proposalId, CancellationToken ct) => throw new NotSupportedException();

        public Task<ProposalRunRecord?> FindByRunAsync(Guid tenantId, string agentId, string runId, CancellationToken ct) => Task.FromResult<ProposalRunRecord?>(null);

        public Task<bool> TransitionAsync(Guid tenantId, Guid proposalId, string fromState, string toState, string actor, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("22003")]
    [InlineData("22P02")]
    [InlineData("22P05")]
    [InlineData("22021")]
    public async Task API_01_body_caused_class_22_database_error_maps_to_400_and_logs_sql_state_only(string sqlState)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddScoped<IProposalStore>(_ => new ThrowingStore(sqlState)));
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken);
        await AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.InvalidRequest, "secret-parameter-value");
        Assert.False(factory.Logs.HasErrorOrAbove);
        Assert.Contains("Proposal insert refused by database data check " + sqlState, factory.Logs.Text);
        Assert.DoesNotContain("secret-parameter-value", factory.Logs.Text);
    }

    // WR-11: class-22 faults a body cannot cause (for example from key-derived values) are server faults, not 400.
    [Theory]
    [InlineData("22001")]
    [InlineData("22008")]
    public async Task API_01_other_class_22_database_error_gives_500(string sqlState)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddScoped<IProposalStore>(_ => new ThrowingStore(sqlState)));
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Fact]
    public async Task API_01_non_class_22_database_error_still_gives_500()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddScoped<IProposalStore>(_ => new ThrowingStore("23505")));
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json(FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
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
    public async Task API_04_run_id_alone_is_not_enough_every_envelope_field_is_required()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", Json("{\"run_id\":\"only\"}"), TestContext.Current.CancellationToken);
        await AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.SchemaViolation);
        Assert.Equal(0, await CountRowsAsync(db, Alpha));
    }

    private static string BodyOfSize(int total)
    {
        const string prefix = "{\"run_id\":\"r\",\"action_type\":\"crm.case_note@1\",\"party_ref\":\"party://p1\",\"parameters\":{\"case_ref\":\"payload://c1\",\"note_ref\":\"payload://n1\"},\"trace_ref\":\"trace://t1\",\"justification\":\"";
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
