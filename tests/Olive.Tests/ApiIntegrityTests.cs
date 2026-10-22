using System.Net;
using Olive.Contracts;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ApiIntegrityTests(PostgresFixture fixture)
{
    private const string Secret = "9007199254740993";

    public static TheoryData<string> GateBodies() => new()
    {
        "{\"run_id\":\"r\",\"justification\":\"a\",\"justification\":\"b\"}",
        "{\"run_id\":\"r\",\"justification\":\"a\",\"justificati\\u006fn\":\"b\"}",
        "{\"run_id\":\"r\",\"parameters\":{\"x\":{\"k\":1,\"k\":2}}}",
        "{\"run_id\":\"r\",\"top\":" + Secret + "}",
        "{\"run_id\":\"r\",\"parameters\":{\"n\":333333333.33333329}}",
        "{\"run_id\":\"r\",\"parameters\":{\"\\ud800\":1}}",
        "{\"run_id\":\"r\",\"parameters\":{\"v\":\"\\udc00\"}}",
        "{\"run_id\":\"r\",\"parameters\":{\"v\":\"\\u0000\"}}",
    };

    [Theory]
    [MemberData(nameof(GateBodies))]
    public async Task API_04_gate_failures_give_400_store_nothing_and_do_not_500(string body)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(body), TestContext.Current.CancellationToken);
        await ApiProposalTests.AssertProblemAsync(r, HttpStatusCode.BadRequest, ProblemReasons.InvalidRequest, Secret, "333333333");
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
        Assert.DoesNotContain("insert", factory.Events);
        Assert.False(factory.Logs.HasErrorOrAbove);
    }

    [Fact]
    public async Task API_04_exact_number_spellings_and_minimal_bodies_still_succeed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var minimal = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json("{\"run_id\":\"r\"}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, minimal.StatusCode);
        var numbers = await alpha.PostAsync("/v1/proposals",
            ApiProposalTests.Json("{\"run_id\":\"r2\",\"parameters\":{\"a\":1.0,\"b\":0.1,\"c\":1e0}}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, numbers.StatusCode);
        var full = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(ApiProposalTests.FullBody), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, full.StatusCode);
        Assert.False(factory.Logs.HasErrorOrAbove);
    }
}
