using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Catalog;
using Olive.Contracts;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class CatalogEndpointTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("fixture-key-liam-alpha")]
    [InlineData("fixture-key-user")]
    [InlineData("fixture-key-application")]
    public async Task API_03_any_active_key_gets_the_catalog(string key)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(key);
        var r = await client.GetAsync("/v1/catalog/1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, doc.RootElement.GetProperty("catalog_version").GetInt32());
        var entry = Assert.Single(doc.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("crm.case_note@1", entry.GetProperty("action_id").GetString());
        Assert.Equal("nexus_crm", entry.GetProperty("target_system").GetString());
        Assert.Equal(JsonValueKind.False, entry.GetProperty("parameters").GetProperty("additionalProperties").ValueKind);
        Assert.Equal("object", entry.GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Array, entry.GetProperty("required_purpose").ValueKind);
        Assert.Equal(JsonValueKind.True, entry.GetProperty("reversible").ValueKind);
        Assert.Equal(JsonValueKind.Number, entry.GetProperty("max_rate").GetProperty("limit").ValueKind);
        Assert.Equal(10, entry.GetProperty("max_rate").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task API_03_inactive_or_missing_key_gets_401()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var inactive = factory.CreateKeyClient("fixture-key-inactive");
        await ApiProposalTests.AssertProblemAsync(await inactive.GetAsync("/v1/catalog/1", TestContext.Current.CancellationToken), HttpStatusCode.Unauthorized, ProblemReasons.KeyInactive);
        using var none = factory.CreateClient();
        await ApiProposalTests.AssertProblemAsync(await none.GetAsync("/v1/catalog/1", TestContext.Current.CancellationToken), HttpStatusCode.Unauthorized, ProblemReasons.KeyInactive);
        // 401 comes before 404.
        await ApiProposalTests.AssertProblemAsync(await inactive.GetAsync("/v1/catalog/99", TestContext.Current.CancellationToken), HttpStatusCode.Unauthorized, ProblemReasons.KeyInactive);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("99999999999999999999")]
    public async Task API_03_unknown_or_unparseable_version_is_404(string version)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient("fixture-key-liam-alpha");
        var r = await client.GetAsync("/v1/catalog/" + version, TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.NotFound, ProblemReasons.CatalogVersionNotFound);
    }

    [Fact]
    public async Task API_10_faulty_catalog_stops_the_host_at_startup()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s =>
        {
            s.AddSingleton<CatalogSet>(_ => throw new InvalidOperationException("Catalog is invalid."));
        });
        var guard = await StartupGuardTests.TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("API-10", guard.Message);
    }

    // WR-01: a catalog fault of any exception type still yields the static API-10 refusal and never echoes its message.
    [Fact]
    public async Task API_10_non_invalid_operation_catalog_fault_refuses_with_static_message()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s =>
        {
            s.AddSingleton<CatalogSet>(_ => throw new Json.Schema.JsonSchemaException("x-hidden secretvalue"));
        });
        var guard = await StartupGuardTests.TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Equal("API-10", guard.Rule);
        Assert.DoesNotContain("secretvalue", guard.Message);
    }

    // WR-05: the submission validator (and its envelope contract) is part of the API-10 guard, not first built on a request.
    [Fact]
    public async Task API_10_faulty_submission_validator_stops_the_host_at_startup()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s =>
        {
            s.AddSingleton<SubmissionValidator>(_ => throw new InvalidOperationException("Contract schema is missing or invalid."));
        });
        var guard = await StartupGuardTests.TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Equal("API-10", guard.Rule);
    }
}
