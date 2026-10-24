using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
using Olive.Catalog;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ExecutionSweepTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<T?> Scalar<T>(TestDatabase db, Guid tenant, string sql, Guid proposal) =>
        ApiProposalTests.ReaderScalarAsync<T>(db, tenant, sql, ("p", proposal));

    private static Task<string?> StateOf(TestDatabase db, Guid tenant, Guid proposal) =>
        Scalar<string>(db, tenant, "select state from proposal where proposal_id = @p", proposal);

    private static string[] Lines(OliveApiFactory factory)
    {
        if (!File.Exists(factory.ExecutorOutputPath)) { return []; }
        using var fs = new FileStream(factory.ExecutorOutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // The fixture set has no beta requester, so the beta analyst is registered here (test-only).
    private static Olive.Integrations.Entra.FixtureUserIdentityResolver Identity()
    {
        var r = Olive.Integrations.Entra.FixtureUserIdentityResolver.CreateDefault();
        r.Register(TestPaths.TenantBeta, "analyst.beta@example.test", "aaaaaaaa-0000-4000-8000-000000000006", "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
        return r;
    }

    // Host 1: a host whose execution step is faulted so approved work is left behind.
    private static OliveApiFactory Host1(TestDatabase db, ExecutionFault fault) =>
        new(db, innerUserIdentity: Identity(), configureServices: s => s.Decorate<IExecutionStore>(real => new FaultingExecutionStore(real, fault, throwOnce: false)));

    // Submits as the tenant's agent, claims and approves as its supervisor. Returns the proposal id.
    private static async Task<Guid> ApproveAsync(OliveApiFactory factory, TestDatabase db, bool beta, string run, int? expiresIn = 3600)
    {
        using var agent = factory.CreateKeyClient(beta ? ApiProposalTests.BetaKey : ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor(run);
        if (expiresIn is { } e) { body = body.TrimEnd('}') + ",\"expires_in_seconds\":" + e + "}"; }
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var proposal = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();
        await db.AdminExecAsync($"update proposal set on_behalf_of = '{(beta ? "analyst.beta" : "analyst.alpha")}@example.test' where proposal_id = '{proposal}'");
        var reviewer = beta ? "beta-sup" : "alpha-sup-1";
        using var rev = factory.CreateReviewerClient(reviewer);
        var item = (await ReviewerTenantTests.ListAsync(rev)).Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid();
        await DecisionHttp.ClaimAsync(factory, reviewer, item);
        var response = await DecisionHttp.Decide(factory, reviewer, item, "approve");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return proposal;
    }

    private static void Start(OliveApiFactory factory) => _ = factory.Services;

    [Fact]
    public async Task EXE_01_sweep_executes_approved_proposals_in_each_mapped_tenant()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Guid a, b;
        string[] host1Lines;
        await using (var h1 = Host1(db, ExecutionFault.ThrowOnBegin))
        {
            a = await ApproveAsync(h1, db, false, "sw1a");
            b = await ApproveAsync(h1, db, true, "sw1b");
            Assert.Equal("approved", await StateOf(db, TestPaths.TenantAlpha, a));
            Assert.Equal("approved", await StateOf(db, TestPaths.TenantBeta, b));
            host1Lines = Lines(h1);
        }

        Assert.Empty(host1Lines);
        await using var h2 = new OliveApiFactory(db);
        Start(h2);

        Assert.Equal("executed", await StateOf(db, TestPaths.TenantAlpha, a));
        Assert.Equal("executed", await StateOf(db, TestPaths.TenantBeta, b));
        var lines = Lines(h2);
        Assert.Equal(2, lines.Length);
        var tenants = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("tenant_id").GetGuid()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { TestPaths.TenantAlpha, TestPaths.TenantBeta }, tenants);
    }

    [Fact]
    public async Task EXE_01_sweep_skips_non_approved_states()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Guid executed, awaiting, rejected;
        await using (var h1 = new OliveApiFactory(db))
        {
            executed = await ApproveAsync(h1, db, false, "sw2a");
            (_, awaiting) = await DecisionHttp.SubmitAsync(h1, db, "sw2b");
            var (item, proposal) = await DecisionHttp.SubmitAsync(h1, db, "sw2c");
            rejected = proposal;
            await DecisionHttp.ClaimAsync(h1, "alpha-sup-1", item);
            Assert.Equal(HttpStatusCode.OK, (await DecisionHttp.Decide(h1, "alpha-sup-1", item, "reject")).StatusCode);
            Assert.Equal("executed", await StateOf(db, TestPaths.TenantAlpha, executed));
        }

        await using var h2 = new OliveApiFactory(db);
        Start(h2);

        Assert.Equal("executed", await StateOf(db, TestPaths.TenantAlpha, executed));
        Assert.Equal("awaiting_approval", await StateOf(db, TestPaths.TenantAlpha, awaiting));
        Assert.Equal("rejected", await StateOf(db, TestPaths.TenantAlpha, rejected));
        Assert.Empty(Lines(h2));
        Assert.Equal(1L, await Scalar<long>(db, TestPaths.TenantAlpha, "select count(*) from execution_ledger where proposal_id = @p and phase = 'started'", executed));
    }

    [Fact]
    public async Task EXE_01_sweep_runs_only_at_startup()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        FaultingExecutionStore? store = null;
        await using var h2 = new OliveApiFactory(db, configureServices: s => s.Decorate<IExecutionStore>(real => store = new FaultingExecutionStore(real)));
        Start(h2);
        _ = h2.Services.GetRequiredService<ExecutionService>();
        store!.Mode = ExecutionFault.ThrowOnBegin;
        store.ThrowOnce = true;
        var proposal = await ApproveAsync(h2, db, false, "sw3");
        Assert.Equal("approved", await StateOf(db, TestPaths.TenantAlpha, proposal));

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.Equal("approved", await StateOf(db, TestPaths.TenantAlpha, proposal));
        Assert.Empty(Lines(h2));
    }

    [Fact]
    public async Task EXE_05_restart_sweep_resolves_in_doubt_without_second_write()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Guid proposal;
        await using (var h1 = Host1(db, ExecutionFault.ThrowOnFinish))
        {
            proposal = await ApproveAsync(h1, db, false, "sw4");
            Assert.Equal("approved", await StateOf(db, TestPaths.TenantAlpha, proposal));
            Assert.Single(Lines(h1));
        }

        await using var h2 = new OliveApiFactory(db);
        Start(h2);

        Assert.Equal("execution_failed", await StateOf(db, TestPaths.TenantAlpha, proposal));
        Assert.Equal(1L, await Scalar<long>(db, TestPaths.TenantAlpha, "select count(*) from execution_ledger where proposal_id = @p and phase = 'finished' and outcome = 'failed' and reason = 'execution_in_doubt'", proposal));
        Assert.Empty(Lines(h2));
    }

    [Fact]
    public async Task EXE_03_restart_with_removed_catalog_entry_ends_catalog_invalid()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Guid proposal;
        await using (var h1 = Host1(db, ExecutionFault.ThrowOnBegin))
        {
            proposal = await ApproveAsync(h1, db, false, "sw5");
        }

        await using var h2 = new OliveApiFactory(db, configureServices: s => s.AddSingleton(new CatalogSet([new CatalogRelease(1, [])])));
        Start(h2);

        Assert.Equal("execution_failed", await StateOf(db, TestPaths.TenantAlpha, proposal));
        Assert.Equal(1L, await Scalar<long>(db, TestPaths.TenantAlpha, "select count(*) from execution_ledger where proposal_id = @p and phase = 'finished' and reason = 'catalog_invalid'", proposal));
        Assert.Empty(Lines(h2));
    }

    [Fact]
    public async Task EXE_03_restart_after_expiry_ends_expired()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Guid proposal;
        await using (var h1 = Host1(db, ExecutionFault.ThrowOnBegin))
        {
            proposal = await ApproveAsync(h1, db, false, "sw6", expiresIn: 3600);
        }

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow.AddHours(3));
        await using var h2 = new OliveApiFactory(db, configureServices: s => s.AddSingleton<TimeProvider>(clock));
        Start(h2);

        Assert.Equal("expired", await StateOf(db, TestPaths.TenantAlpha, proposal));
        Assert.Equal(1L, await Scalar<long>(db, TestPaths.TenantAlpha, "select count(*) from execution_ledger where proposal_id = @p and phase = 'finished' and reason = 'proposal_expired'", proposal));
        Assert.Empty(Lines(h2));
    }

    [Fact]
    public async Task EXE_04_failing_startup_guard_prevents_sweep_execution()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Guid proposal;
        await using (var h1 = Host1(db, ExecutionFault.ThrowOnBegin))
        {
            proposal = await ApproveAsync(h1, db, false, "sw8");
            Assert.Equal("approved", await StateOf(db, TestPaths.TenantAlpha, proposal));
        }

        // D-19 guard: the migrator URL key present in the service configuration refuses start.
        await using var h2 = new OliveApiFactory(db, extraConfig: new Dictionary<string, string?> { ["OLIVE_MIGRATOR_URL"] = "" });
        Assert.ThrowsAny<Exception>(() => h2.Services);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.Equal("approved", await StateOf(db, TestPaths.TenantAlpha, proposal));
        Assert.Equal(0L, await Scalar<long>(db, TestPaths.TenantAlpha, "select count(*) from execution_ledger where proposal_id = @p", proposal));
        Assert.Empty(Lines(h2));
    }

    [Fact]
    public async Task EXE_01_sweep_fault_does_not_stop_host_start()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        Guid proposal;
        await using (var h1 = Host1(db, ExecutionFault.ThrowOnBegin))
        {
            proposal = await ApproveAsync(h1, db, false, "sw7");
        }

        await using var h2 = Host1(db, ExecutionFault.ThrowOnBegin);
        using var client = h2.CreateKeyClient(ApiProposalTests.AlphaKey);
        var response = await client.GetAsync($"/v1/proposals/{proposal}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("approved", await StateOf(db, TestPaths.TenantAlpha, proposal));
        Assert.DoesNotContain(FaultingExecutionStore.Sentinel, h2.Logs.Text);
        Assert.Empty(Lines(h2));
    }
}
