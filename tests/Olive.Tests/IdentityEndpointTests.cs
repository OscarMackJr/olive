using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class IdentityEndpointTests(PostgresFixture fixture)
{
    private const string Body = ApiProposalTests.FullBody;
    private static StringContent Json(string s) => ApiProposalTests.Json(s);

    private sealed class FixedIntrospector(IntrospectionResult result) : IKeyIntrospector
    {
        public Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct) => Task.FromResult(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer a b")]
    [InlineData("Bearer unknown-key")]
    [InlineData("Bearer fixture-key-inactive")]
    public async Task IDN_01_inactive_unknown_or_malformed_key_gives_identical_401(string? header)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateClient();
        if (header is not null) { client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header); }

        var r = await client.PostAsync("/v1/proposals", Json(Body), TestContext.Current.CancellationToken);
        var problem = await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Unauthorized, ProblemReasons.KeyInactive, "unknown-key", "fixture-key-inactive");
        Assert.Equal("urn:olive:problem:key_inactive", problem.GetProperty("type").GetString());
        Assert.Contains("Bearer", r.Headers.WwwAuthenticate.Select(h => h.Scheme));
        Assert.DoesNotContain("insert", factory.Events);
        Assert.DoesNotContain("get", factory.Events);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Fact]
    public async Task IDN_01_get_without_key_gives_401()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateClient();
        var r = await client.GetAsync($"/v1/proposals/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Unauthorized, ProblemReasons.KeyInactive);
        Assert.DoesNotContain("get", factory.Events);
    }

    [Fact]
    public async Task IDN_01_inactive_key_with_invalid_body_is_401_not_400()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient("fixture-key-inactive");
        var r = await client.PostAsync("/v1/proposals", Json("not json at all"), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Unauthorized, ProblemReasons.KeyInactive);
        Assert.Equal(["introspect"], factory.Events);
    }

    [Fact]
    public async Task IDN_01_exactly_one_introspect_precedes_insert()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await client.PostAsync("/v1/proposals", Json(Body), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(["introspect", "FindByRun", "token", "resolve", "insert"], factory.Events);
        Assert.Equal(1, factory.Introspector.Calls);
    }

    [Fact]
    public async Task IDN_01_expired_key_from_introspector_gives_401()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var expired = new ActiveKey("0a1b2c3d-0000-4000-8000-0000000000ff", PrincipalTypes.Agent, "liam", "x@example.test", "servicing",
            ApiProposalTests.Alpha, "confidential-personal", DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var factory = new OliveApiFactory(db, new FixedIntrospector(IntrospectionResult.From(expired)));
        using var client = factory.CreateKeyClient("anything");
        var r = await client.PostAsync("/v1/proposals", Json(Body), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Unauthorized, ProblemReasons.KeyInactive);
        Assert.DoesNotContain("insert", factory.Events);
    }

    [Fact]
    public async Task IDN_01_introspection_unavailable_fails_closed_with_503()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, new ThrowingKeyIntrospector());
        using var client = factory.CreateKeyClient("anything-secret-value");
        var r = await client.PostAsync("/v1/proposals", Json(Body), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.ServiceUnavailable, ProblemReasons.IntrospectionUnavailable, "anything-secret-value", "test_unavailable");
        Assert.DoesNotContain("insert", factory.Events);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        Assert.DoesNotContain("anything-secret-value", factory.Logs.Text);
    }

    private sealed class BrokenIntrospector : IKeyIntrospector
    {
        public Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct) =>
            throw new InvalidOperationException("secret-internal-detail");
    }

    [Fact]
    public async Task IDN_01_unexpected_failure_gives_generic_500_without_exception_text()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, new BrokenIntrospector());
        using var client = factory.CreateKeyClient("some-session-key");
        var r = await client.PostAsync("/v1/proposals", Json(Body), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.InternalServerError, ProblemReasons.InternalError, "secret-internal-detail", "some-session-key");
        Assert.DoesNotContain("secret-internal-detail", factory.Logs.Text);
        Assert.DoesNotContain("some-session-key", factory.Logs.Text);
    }

    [Theory]
    [InlineData("fixture-key-user", ProblemReasons.PrincipalNotPermitted)]
    [InlineData("fixture-key-application", ProblemReasons.PrincipalNotPermitted)]
    [InlineData("fixture-key-agent-not-allowlisted", ProblemReasons.AgentNotAllowlisted)]
    public async Task IDN_02_non_agent_or_non_allowlisted_post_gives_403_and_stores_nothing(string key, string reason)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(key);
        var r = await client.PostAsync("/v1/proposals", Json(Body), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, reason);
        Assert.DoesNotContain("insert", factory.Events);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Theory]
    [InlineData("fixture-key-user", ProblemReasons.PrincipalNotPermitted)]
    [InlineData("fixture-key-agent-not-allowlisted", ProblemReasons.AgentNotAllowlisted)]
    public async Task IDN_02_principal_check_precedes_body_validation(string key, string reason)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(key);
        var r = await client.PostAsync("/v1/proposals", Json("not json"), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, reason);
    }

    [Fact]
    public async Task IDN_02_get_works_for_any_active_key_within_its_tenant()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var id = await db.SeedProposalAsync(ApiProposalTests.Alpha, "liam", "seeded-run");
        using var user = factory.CreateKeyClient("fixture-key-user");
        var r = await user.GetAsync($"/v1/proposals/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task IDN_04_stored_row_records_key_identity_and_advisory_session_id()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor("run-9", nonoSessionId: "nono-9");
        var r = await client.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("proposal_id").GetGuid();

        async Task<string?> Col(string sql) =>
            await ApiProposalTests.ReaderScalarAsync<string>(db, ApiProposalTests.Alpha, sql, ("p", id));
        Assert.Equal("0a1b2c3d-0000-4000-8000-000000000001", await Col("select key_id from proposal where proposal_id = @p"));
        Assert.Equal("analyst.alpha@example.test", await Col("select on_behalf_of from proposal where proposal_id = @p"));
        Assert.Equal("servicing", await Col("select purpose from proposal where proposal_id = @p"));
        Assert.Equal("liam", await Col("select agent_id from proposal where proposal_id = @p"));
        Assert.Equal("nono-9", await Col("select nono_session_id from proposal where proposal_id = @p"));
        Assert.Equal(ApiProposalTests.Alpha.ToString(), await Col("select tenant_id::text from proposal where proposal_id = @p"));
        Assert.Equal("2099-01-01T00:00:00Z", await Col("select to_char(key_expires_at at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"') from proposal where proposal_id = @p"));
        Assert.Equal("submitted", await Col("select event_type from proposal_event where proposal_id = @p"));
    }

    [Fact]
    public async Task IDN_04_body_tenant_is_ignored()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        // Identity fields in the body are refused outright by the closed envelope (API-04); nothing is stored for either tenant.
        var spoofed = ApiProposalTests.BodyFor("run-s")[..^1] + $",\"tenant_id\":\"{ApiProposalTests.Beta}\",\"agent_id\":\"mallory\"}}";
        var refused = await client.PostAsync("/v1/proposals", Json(spoofed), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(refused, HttpStatusCode.BadRequest, ProblemReasons.SchemaViolation);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));

        // A tenant-looking value in an allowed field is only advisory data; the tenant still comes from the key.
        var body = ApiProposalTests.BodyFor("run-t", nonoSessionId: ApiProposalTests.Beta.ToString());
        var r = await client.PostAsync("/v1/proposals", Json(body), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(1, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Beta));
        var agent = await ApiProposalTests.ReaderScalarAsync<string>(db, ApiProposalTests.Alpha, "select agent_id from proposal");
        Assert.Equal("liam", agent);
    }
}
