using System.Net;
using System.Text.Json;
using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueEventsTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetEventsAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"{Items}/{id}/events", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task QUE_09_accepted_submission_has_one_created_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "ev1");
        using var alpha = factory.CreateReviewerClient("alpha-norole");
        var id = Assert.Single(await ReviewerTenantTests.ListAsync(alpha)).GetProperty("item_id").GetGuid();

        var root = await GetEventsAsync(alpha, id);
        var e = Assert.Single(root.GetProperty("events").EnumerateArray());
        Assert.Equal("created", e.GetProperty("event_type").GetString());
        Assert.Equal("open", e.GetProperty("to_state").GetString());
        Assert.True(e.GetProperty("from_state").ValueKind is JsonValueKind.Null || !e.TryGetProperty("from_state", out _));
        Assert.False(string.IsNullOrEmpty(e.GetProperty("actor").GetString()));
        Assert.True(e.GetProperty("seq").GetInt64() > 0);
        Assert.False(e.TryGetProperty("rationale", out _));
    }

    [Fact]
    public async Task QUE_09_events_are_tenant_scoped_and_agents_are_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "ev2");
        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var id = Assert.Single(await ReviewerTenantTests.ListAsync(alpha)).GetProperty("item_id").GetGuid();

        using var beta = factory.CreateReviewerClient("beta-sup");
        var foreign = await beta.GetAsync($"{Items}/{id}/events", Ct);
        var unknown = await beta.GetAsync($"{Items}/{Guid.NewGuid()}/events", Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var foreignBody = await foreign.Content.ReadAsStringAsync(Ct);
        Assert.Contains("item_not_found", foreignBody);
        Assert.Equal(foreignBody, await unknown.Content.ReadAsStringAsync(Ct));

        var agentResponse = await agent.GetAsync($"{Items}/{id}/events", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, agentResponse.StatusCode);
        Assert.Contains("principal_not_permitted", await agentResponse.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task QUE_09_events_are_ordered_by_seq_even_when_created_at_ties()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "ev3");
        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var id = Assert.Single(await ReviewerTenantTests.ListAsync(alpha)).GetProperty("item_id").GetGuid();

        // One transaction, so now() (created_at) is identical for both inserted rows; event_id order is the reverse of insertion.
        var low = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var high = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        await using (var conn = new NpgsqlConnection(db.AppConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var tx = await conn.BeginTransactionAsync(Ct);
            await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
            {
                set.Parameters.AddWithValue("t", ApiProposalTests.Alpha.ToString());
                await set.ExecuteNonQueryAsync(Ct);
            }

            foreach (var (eventId, type, from, to) in new[] { (high, "claimed", "open", "claimed"), (low, "released", "claimed", "open") })
            {
                await using var ins = new NpgsqlCommand(
                    "insert into queue_item_event (tenant_id, event_id, item_id, event_type, from_state, to_state, actor) values (@t, @e, @i, @ty, @f, @to, 'tester')", conn, tx);
                ins.Parameters.AddWithValue("t", ApiProposalTests.Alpha);
                ins.Parameters.AddWithValue("e", eventId);
                ins.Parameters.AddWithValue("i", id);
                ins.Parameters.AddWithValue("ty", type);
                ins.Parameters.AddWithValue("f", from);
                ins.Parameters.AddWithValue("to", to);
                await ins.ExecuteNonQueryAsync(Ct);
            }

            await tx.CommitAsync(Ct);
        }

        var root = await GetEventsAsync(alpha, id);
        var events = root.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(["created", "claimed", "released"], events.Select(e => e.GetProperty("event_type").GetString()).ToArray());
        var seqs = events.Select(e => e.GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal(seqs.OrderBy(s => s).ToArray(), seqs);
        Assert.Equal(events[1].GetProperty("created_at").GetDateTimeOffset(), events[2].GetProperty("created_at").GetDateTimeOffset());
    }
}
