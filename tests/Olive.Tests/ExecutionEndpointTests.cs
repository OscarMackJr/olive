using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ExecutionEndpointTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] JsonlProperties =
        ["action_id", "executed_at", "execution_mode", "proposal_id", "target_system", "tenant_id", "would_send"];

    private static string[] Lines(OliveApiFactory factory)
    {
        if (!File.Exists(factory.ExecutorOutputPath)) { return []; }
        using var fs = new FileStream(factory.ExecutorOutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static Task<long> LedgerCount(TestDatabase db, Guid proposal, string? phase = null) =>
        DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and (@ph::text is null or phase = @ph)",
            ("p", proposal), ("ph", (object?)phase ?? DBNull.Value))!;

    private static async Task<(Guid Item, Guid Proposal)> SubmitClaimed(OliveApiFactory factory, TestDatabase db, string run)
    {
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, run);
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        return (item, proposal);
    }

    private static string[] PropertyNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task EXE_01_approve_executes_after_commit_and_state_becomes_executed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await SubmitClaimed(factory, db, "e1a");

        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);

        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        using var got = JsonDocument.Parse(await (await agent.GetAsync($"/v1/proposals/{proposal}", Ct)).Content.ReadAsStringAsync(Ct));
        Assert.Equal("executed", got.RootElement.GetProperty("state").GetString());
        Assert.Equal(1L, await LedgerCount(db, proposal, "started"));
        Assert.Equal(1L, await LedgerCount(db, proposal, "finished"));
        Assert.Equal(1L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and phase = 'finished' and outcome = 'succeeded' and execution_mode = 'simulated'", ("p", proposal)));

        var lines = Lines(factory);
        var line = Assert.Single(lines);
        Assert.Equal(JsonlProperties, PropertyNames(line));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(DecisionHttp.Alpha, doc.RootElement.GetProperty("tenant_id").GetGuid());
        Assert.Equal(proposal, doc.RootElement.GetProperty("proposal_id").GetGuid());
        Assert.Equal("simulated", doc.RootElement.GetProperty("execution_mode").GetString());
    }

    [Fact]
    public async Task EXE_01_decide_response_shape_is_unchanged()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await SubmitClaimed(factory, db, "e1b");
        var (item2, _) = await SubmitClaimed(factory, db, "e1c");

        var approve = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");
        var reject = await DecisionHttp.Decide(factory, "alpha-sup-1", item2, "reject");

        Assert.Equal(PropertyNames(await reject.Content.ReadAsStringAsync(Ct)), PropertyNames(await approve.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task EXE_01_reject_does_not_execute()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await SubmitClaimed(factory, db, "e1d");

        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "reject")).StatusCode);

        Assert.Equal("rejected", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(0L, await LedgerCount(db, proposal));
        Assert.Empty(Lines(factory));
    }

    [Fact]
    public async Task EXE_02_repeat_approve_returns_first_result_without_second_write()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await SubmitClaimed(factory, db, "e2a");

        var first = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");
        var again = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(Lines(factory));
        Assert.Equal(2L, await LedgerCount(db, proposal));
        Assert.Equal("executed", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task EXE_02_direct_second_attempt_returns_replayed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await SubmitClaimed(factory, db, "e2b");
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);

        var service = factory.Services.GetRequiredService<ExecutionService>();
        var replay = await service.ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);
        var again = await service.ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);

        var r1 = Assert.IsType<ExecutionResult.Replayed>(replay);
        var r2 = Assert.IsType<ExecutionResult.Replayed>(again);
        Assert.Equal(r1.Row.ExecutionId, r2.Row.ExecutionId);
        Assert.Equal("finished", r1.Row.Phase);
        Assert.Single(Lines(factory));
    }

    [Fact]
    public async Task EXE_01_proposal_get_response_has_no_new_fields()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await SubmitClaimed(factory, db, "e1e");
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);

        var beforeText = await (await agent.GetAsync($"/v1/proposals/{proposal}", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);
        var afterText = await (await agent.GetAsync($"/v1/proposals/{proposal}", Ct)).Content.ReadAsStringAsync(Ct);

        Assert.Equal(PropertyNames(beforeText), PropertyNames(afterText));
        using var before = JsonDocument.Parse(beforeText);
        using var after = JsonDocument.Parse(afterText);
        Assert.Equal("awaiting_approval", before.RootElement.GetProperty("state").GetString());
        Assert.Equal("executed", after.RootElement.GetProperty("state").GetString());
    }
}
