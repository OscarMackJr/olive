using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Olive.Contracts;

namespace Olive.Tests;

// Reference-only invariant across the whole reviewer surface (QUE-01, D-05, D-03).
[Trait("Category", "Integration")]
public sealed class QueueSurfaceInvariantTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private const string ParamSentinel = "SENTINEL-QSURF-PARAM-4417";
    private const string JustSentinel = "SENTINEL-QSURF-JUSTIFICATION-4417";
    private const string OnBehalfOf = "analyst.alpha@example.test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Forbidden = ["parameters", "justification", "on_behalf_of", "rationale"];

    private static string[] JsonNames(Type t) => t.GetProperties()
        .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name).ToArray();

    [Fact]
    public void QUE_01_only_the_payload_response_exposes_content_members_and_no_dto_exposes_rationale()
    {
        var dtos = typeof(QueueItemDto).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(QueueItemDto).Namespace && t.Name.StartsWith("Queue", StringComparison.Ordinal) || t == typeof(ReviewerPayloadResponse)).ToArray();
        Assert.Contains(typeof(QueueItemEventDto), dtos);
        foreach (var dto in dtos.Where(d => d != typeof(ReviewerPayloadResponse)))
        {
            var names = JsonNames(dto);
            Assert.DoesNotContain("parameters", names);
            Assert.DoesNotContain("justification", names);
            Assert.DoesNotContain("on_behalf_of", names);
            Assert.DoesNotContain("rationale", names);
        }

        Assert.Contains("parameters", JsonNames(typeof(ReviewerPayloadResponse)));
        Assert.DoesNotContain("rationale", JsonNames(typeof(ReviewerPayloadResponse)));
        // The decision request is the single write-only place a rationale may exist.
        Assert.Contains("rationale", JsonNames(typeof(DecisionRequest)));
    }

    private static void AssertNoContent(string label, string body)
    {
        foreach (var s in new[] { ParamSentinel, JustSentinel, OnBehalfOf, DecisionHttp.Rationale })
        {
            Assert.False(body.Contains(s, StringComparison.Ordinal), $"{label} leaked a content sentinel (length {s.Length})");
        }

        using var doc = JsonDocument.Parse(body);
        AssertNoMembers(label, doc.RootElement);
    }

    private static void AssertNoMembers(string label, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    Assert.False(Forbidden.Contains(p.Name), $"{label} exposes member {p.Name}");
                    AssertNoMembers(label, p.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray()) { AssertNoMembers(label, i); }
                break;
        }
    }

    [Fact]
    public async Task QUE_01_responses_across_the_surface_carry_no_content_except_the_role_gated_payload()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = "{\"run_id\":\"qsurf\",\"action_type\":\"crm.case_note@1\",\"party_ref\":\"party://p1\",\"parameters\":{\"case_ref\":\"payload://" + ParamSentinel
            + "\",\"note_ref\":\"payload://n1\"},\"justification\":\"" + JustSentinel + "\",\"trace_ref\":\"trace://t1\"}";
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var postText = await post.Content.ReadAsStringAsync(Ct);
        var proposal = JsonDocument.Parse(postText).RootElement.GetProperty("proposal_id").GetGuid();
        await db.AdminExecAsync($"update proposal set on_behalf_of = '{OnBehalfOf}' where proposal_id = '{proposal}'");

        using var sup = factory.CreateReviewerClient("alpha-sup-1");
        var listResponse = await sup.GetAsync(Items, Ct);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listText = await listResponse.Content.ReadAsStringAsync(Ct);
        AssertNoContent("list", listText);
        var item = JsonDocument.Parse(listText).RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid();

        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        var decision = await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve");
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        AssertNoContent("decision", await decision.Content.ReadAsStringAsync(Ct));

        var get = await sup.GetAsync($"{Items}/{item}", Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertNoContent("get", await get.Content.ReadAsStringAsync(Ct));

        var events = await sup.GetAsync($"{Items}/{item}/events", Ct);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        AssertNoContent("events", await events.Content.ReadAsStringAsync(Ct));

        using var norole = factory.CreateReviewerClient("alpha-norole");
        var denied = await norole.GetAsync($"{Items}/{item}/payload", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        AssertNoContent("payload (no role)", await denied.Content.ReadAsStringAsync(Ct));

        var payload = await sup.GetAsync($"{Items}/{item}/payload", Ct);
        Assert.Equal(HttpStatusCode.OK, payload.StatusCode);
        var payloadText = await payload.Content.ReadAsStringAsync(Ct);
        Assert.Contains(ParamSentinel, payloadText);
        Assert.DoesNotContain(DecisionHttp.Rationale, payloadText);
    }

    [Fact]
    public async Task D_03_only_the_producer_only_create_route_exists_and_there_is_no_withdraw_route()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateClient();

        var routes = factory.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (Method: m, Pattern: e.RoutePattern.RawText ?? "")))
            .ToArray();

        Assert.Contains(routes, r => r.Method == "GET" && r.Pattern.TrimEnd('/') == Items);
        // Phase 8 (IRQ-01): exactly one POST on the collection, the service-token create route (ProducerOnly filter, asserted in IdentityReviewCreateTests).
        Assert.Single(routes, r => r.Method == "POST" && r.Pattern.TrimEnd('/') == Items);
        Assert.DoesNotContain(routes, r => r.Pattern.EndsWith("/withdraw", StringComparison.Ordinal));
    }
}
