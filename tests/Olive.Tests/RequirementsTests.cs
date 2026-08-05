using System.Text.Json;
using Olive;

namespace Olive.Tests;

public sealed class RequirementsTests
{
    private static readonly Guid Tenant = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");
    private static readonly Guid OtherTenant = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static ActionBroker Broker(ReviewQueueService? queue = null, string? output = null)
    {
        var catalog = Catalog.Load(Path.Combine(Root, "catalog"), Path.Combine(Root, "contracts", "vendored", "vocabulary.yaml"));
        return new ActionBroker(catalog, new PolicyEngine(), queue ?? new ReviewQueueService(), new SimulatedActionExecutor(output ?? Path.Combine(Path.GetTempPath(), $"olive-{Guid.NewGuid()}.jsonl")));
    }
    private static Proposal Proposal(string purpose = "servicing", DateTimeOffset? expires = null, Principal? principal = null) => new(Guid.NewGuid(), Tenant, "crm.case_note@1", JsonDocument.Parse("{}").RootElement, "trace://test", principal ?? new("user", "user-1", Grants: new HashSet<string> { purpose }), purpose, expires ?? DateTimeOffset.UtcNow.AddHours(1));

    [Fact] public void REQ_ACT_01_execution_traces_to_proposal_policy_and_named_approver() { var b = Broker(); var p = b.Submit(Proposal(), DateTimeOffset.UtcNow); var a = b.Approve(Tenant, p.ProposalId, "supervisor-1", "reviewed", DateTimeOffset.UtcNow); var e = b.Execute(Tenant, p.ProposalId, DateTimeOffset.UtcNow); Assert.Equal(p.ProposalId, e.ProposalId); Assert.NotEmpty(a.Decisions!); Assert.Equal("supervisor-1", e.Approver); }
    [Fact] public void REQ_ACT_02_policy_refusal_happens_before_queueing() { var q = new ReviewQueueService(); var p = Broker(q).Submit(Proposal("marketing"), DateTimeOffset.UtcNow); Assert.Equal(Outcomes.RefusedPolicy, p.State); Assert.Empty(q.List(Tenant)); Assert.Contains(p.Decisions!, x => x.Check == "purpose_entitlement" && x.Outcome == "refused"); }
    [Fact] public void REQ_ACT_03_execution_is_idempotent_on_proposal_id() { var output = Path.Combine(Path.GetTempPath(), $"olive-{Guid.NewGuid()}.jsonl"); var b = Broker(output: output); var p = b.Submit(Proposal(), DateTimeOffset.UtcNow); b.Approve(Tenant, p.ProposalId, "supervisor", "ok", DateTimeOffset.UtcNow); var first = b.Execute(Tenant, p.ProposalId, DateTimeOffset.UtcNow); var second = b.Execute(Tenant, p.ProposalId, DateTimeOffset.UtcNow); Assert.Equal(first.ExecutionId, second.ExecutionId); Assert.Single(File.ReadAllLines(output)); }
    [Fact] public void REQ_ACT_04_no_external_write_connector_exists() { var source = File.ReadAllText(Path.Combine(Root, "src", "Olive", "Domain.cs")); Assert.DoesNotContain("HttpClient", source); Assert.Contains("SimulatedActionExecutor", source); }
    [Fact] public void REQ_ACT_05_expired_proposals_cannot_execute() { var b = Broker(); var p = b.Submit(Proposal(expires: DateTimeOffset.UtcNow.AddMinutes(-1)), DateTimeOffset.UtcNow); var a = b.Approve(Tenant, p.ProposalId, "supervisor", "ok", DateTimeOffset.UtcNow); Assert.Equal(Outcomes.Expired, a.State); Assert.Throws<InvalidOperationException>(() => b.Execute(Tenant, p.ProposalId, DateTimeOffset.UtcNow)); }
    [Fact] public void REQ_ACT_06_runtime_added_action_type_is_rejected() { var c = Catalog.Load(Path.Combine(Root, "catalog"), Path.Combine(Root, "contracts", "vendored", "vocabulary.yaml")); Assert.Throws<NotSupportedException>(() => c.AddAtRuntime(new("x.y@1", "x", new HashSet<string> { "servicing" }, "role:x", true, 1, TimeSpan.FromMinutes(1)))); }
    [Fact] public void REQ_QUE_01_inline_payload_is_rejected_by_reference_only_schema() { using var invalid = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "contracts", "examples", "queue-item.invalid.json"))); using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "contracts", "schemas", "queue-item.schema.json"))); var allowed = schema.RootElement.GetProperty("properties").EnumerateObject().Select(x => x.Name).ToHashSet(); Assert.Equal(JsonValueKind.False, schema.RootElement.GetProperty("additionalProperties").ValueKind == JsonValueKind.False ? JsonValueKind.False : JsonValueKind.True); Assert.False(invalid.RootElement.EnumerateObject().All(x => allowed.Contains(x.Name))); }
    [Fact] public void REQ_QUE_02_database_rls_scopes_when_application_predicate_is_removed() { var q = new ReviewQueueService(); q.Add(new(Guid.NewGuid(), Tenant, "x", "subject://1", "payload://1")); q.Add(new(Guid.NewGuid(), OtherTenant, "x", "subject://2", "payload://2")); Assert.Single(q.ListWithDatabaseRls(Tenant)); }
    [Fact] public void REQ_QUE_03_decision_requires_who_when_and_why() { var q = new ReviewQueueService(); var i = new QueueItem(Guid.NewGuid(), Tenant, "x", "subject://1", "payload://1"); q.Add(i); Assert.Throws<ArgumentException>(() => q.Decide(Tenant, i.ItemId, "", Outcomes.Approved, "", DateTimeOffset.UtcNow)); }
    [Fact] public void REQ_QUE_04_bluto_ambiguous_match_routes_to_shared_queue() { var q = new ReviewQueueService(); q.Add(new(Guid.NewGuid(), Tenant, "bluto_identity_review", "bluto://ambiguous/1", "bluto://review/1")); Assert.Equal("bluto_identity_review", q.List(Tenant).Single().Kind); }
    [Fact] public void policy_decision_detail_never_contains_parameter_value() { var value = "must-not-appear"; var p = Proposal() with { Parameters = JsonDocument.Parse($"{{\"note\":\"{value}\"}}").RootElement }; var r = Broker().Submit(p, DateTimeOffset.UtcNow); Assert.DoesNotContain(r.Decisions!, x => x.Detail.Contains(value)); }
}
