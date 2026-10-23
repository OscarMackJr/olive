using Npgsql;
using Olive.Application;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ProposalStoreRefusalTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NewProposal Sample(string run = "run-1") => new(
        "agent-1", "key-1", "user-1", "case_management", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
        "nono-1", run, "crm.case_note@1", "party://p1", "{\"a\":1}", "because", "trace://t1",
        TraceVerification: "fixture", TraceSchemaVersion: "ate/1.0");

    private static PostgresProposalStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)));

    private static async Task<List<(string Type, string? From, string To)>> EventsAsync(TestDatabase db, Guid id)
    {
        await using var ds = NpgsqlDataSource.Create(db.ReaderConnectionString);
        return await new TenantUnitOfWork(ds).ExecuteAsync(TestPaths.TenantAlpha, async (conn, tx) =>
        {
            var list = new List<(string, string?, string)>();
            await using var cmd = new NpgsqlCommand("select event_type, from_state, to_state from proposal_event where proposal_id = @p", conn, tx);
            cmd.Parameters.AddWithValue("p", id);
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct))
            {
                list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
            }

            return list;
        }, Ct);
    }

    [Fact]
    public async Task TRC_03_insert_refused_stores_state_reason_trace_fields_and_both_events()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = await store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample(), "trace_not_found", RefusalKind.Trace, Ct);
        Assert.NotNull(id);

        var rec = await store.GetAsync(TestPaths.TenantAlpha, id!.Value, Ct);
        Assert.NotNull(rec);
        Assert.Equal("refused_policy", rec!.State);
        Assert.Equal("trace_not_found", rec.RefusalReason);
        Assert.Equal("fixture", rec.TraceVerification);
        Assert.Equal("ate/1.0", rec.TraceSchemaVersion);

        var events = await EventsAsync(db, id.Value);
        Assert.Equal(2, events.Count);
        Assert.Contains(("submitted", (string?)null, "proposed"), events);
        Assert.Contains(("trace_refused", (string?)"proposed", "refused_policy"), events);
    }

    [Fact]
    public async Task POL_07_expires_at_round_trips_as_the_same_utc_instant_and_legacy_is_null()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var at = new DateTimeOffset(2030, 1, 2, 11, 4, 5, TimeSpan.Zero);
        var withExpiry = await store.InsertAsync(TestPaths.TenantAlpha, Sample("run-e") with { ExpiresAt = at }, Ct);
        var without = await store.InsertAsync(TestPaths.TenantAlpha, Sample("run-n"), Ct);
        Assert.Equal(at, (await store.GetAsync(TestPaths.TenantAlpha, withExpiry!.Value, Ct))!.ExpiresAt);
        Assert.Null((await store.GetAsync(TestPaths.TenantAlpha, without!.Value, Ct))!.ExpiresAt);
    }

    [Fact]
    public async Task POL_04_policy_refusal_writes_policy_evaluated_and_policy_refused_events()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var detail = "{\"outcome\":\"refused\",\"reason\":\"not_in_catalog\",\"checks\":[{\"name\":\"catalog\",\"result\":\"fail\"}]}";
        var id = await store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample(), "not_in_catalog", RefusalKind.Policy, Ct, detail);
        Assert.NotNull(id);

        var events = await EventsAsync(db, id!.Value);
        Assert.Equal(3, events.Count);
        Assert.Contains(("submitted", (string?)null, "proposed"), events);
        Assert.Contains(("policy_evaluated", (string?)null, "proposed"), events);
        Assert.Contains(("policy_refused", (string?)"proposed", "refused_policy"), events);
        Assert.DoesNotContain(events, e => e.Type == "trace_refused");
        Assert.Equal("not_in_catalog", (await store.GetAsync(TestPaths.TenantAlpha, id.Value, Ct))!.RefusalReason);
    }

    // WR-02: the event type comes from the explicit kind; a kind/detail mismatch is rejected before any database work.
    [Fact]
    public async Task POL_04_refusal_kind_and_detail_mismatch_is_rejected_and_nothing_is_stored()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        await Assert.ThrowsAsync<ArgumentException>(() => store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample("run-p"), "purpose_not_permitted", RefusalKind.Policy, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample("run-t"), "trace_not_found", RefusalKind.Trace, Ct, "{}"));
        Assert.Null(await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "run-p", Ct));
        Assert.Null(await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "run-t", Ct));
    }

    [Fact]
    public async Task TRC_03_transition_into_or_out_of_refused_policy_is_rejected_before_the_database()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var accepted = await store.InsertAsync(TestPaths.TenantAlpha, Sample("run-ok"), Ct);
        var refused = await store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample("run-refused"), "trace_not_found", RefusalKind.Trace, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => store.TransitionAsync(TestPaths.TenantAlpha, accepted!.Value, "proposed", "refused_policy", "sys", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TransitionAsync(TestPaths.TenantAlpha, refused!.Value, "refused_policy", "proposed", "sys", Ct));

        Assert.Equal("proposed", (await store.GetAsync(TestPaths.TenantAlpha, accepted!.Value, Ct))!.State);
        var still = (await store.GetAsync(TestPaths.TenantAlpha, refused!.Value, Ct))!;
        Assert.Equal("refused_policy", still.State);
        Assert.Equal("trace_not_found", still.RefusalReason);
        Assert.Single(await EventsAsync(db, accepted.Value));
        Assert.Equal(2, (await EventsAsync(db, refused.Value)).Count);
    }

    [Fact]
    public async Task TRC_03_duplicate_run_returns_null_and_writes_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var first = await store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample(), "trace_not_found", RefusalKind.Trace, Ct);
        Assert.NotNull(first);
        Assert.Null(await store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample(), "trace_not_found", RefusalKind.Trace, Ct));
        Assert.Null(await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct));
        Assert.Equal(2, (await EventsAsync(db, first!.Value)).Count);
    }

    [Fact]
    public async Task TRC_03_find_by_run_returns_refusal_reason_and_null_for_accepted_rows()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        await store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample("run-refused"), "trace_not_found", RefusalKind.Trace, Ct);
        await store.InsertAsync(TestPaths.TenantAlpha, Sample("run-ok"), Ct);

        var refused = await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "run-refused", Ct);
        Assert.Equal("refused_policy", refused!.State);
        Assert.Equal("trace_not_found", refused.RefusalReason);
        var accepted = await store.FindByRunAsync(TestPaths.TenantAlpha, "agent-1", "run-ok", Ct);
        Assert.Equal("proposed", accepted!.State);
        Assert.Null(accepted.RefusalReason);
    }

    [Fact]
    public async Task TRC_09_accepted_insert_carries_trace_fields_and_no_reason()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct);
        var rec = await store.GetAsync(TestPaths.TenantAlpha, id!.Value, Ct);
        Assert.Equal("proposed", rec!.State);
        Assert.Null(rec.RefusalReason);
        Assert.Equal("fixture", rec.TraceVerification);
        Assert.Equal("ate/1.0", rec.TraceSchemaVersion);
    }

    [Fact]
    public async Task TRC_03_cross_tenant_get_of_refused_row_returns_null()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = await store.InsertRefusedAsync(TestPaths.TenantAlpha, Sample(), "trace_not_found", RefusalKind.Trace, Ct);
        Assert.Null(await store.GetAsync(TestPaths.TenantBeta, id!.Value, Ct));
        Assert.Null(await store.FindByRunAsync(TestPaths.TenantBeta, "agent-1", "run-1", Ct));
    }
}
