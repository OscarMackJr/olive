using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Olive;

public static class Outcomes
{
    public const string Proposed = "proposed", RefusedPolicy = "refused_policy", AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved", Rejected = "rejected", Executed = "executed", ExecutionFailed = "execution_failed", Expired = "expired";
}

public sealed record Principal(string Type, string Id, Principal? OnBehalfOf = null, IReadOnlySet<string>? Grants = null);
public sealed record ActionType(string ActionId, string TargetSystem, IReadOnlySet<string> RequiredPurposes, string RequiredApproval, bool Reversible, int RateLimit, TimeSpan RateWindow);
public sealed record Proposal(Guid ProposalId, Guid TenantId, string ActionId, JsonElement Parameters, string TraceRef, Principal Principal, string Purpose, DateTimeOffset ExpiresAt, string State = Outcomes.Proposed, IReadOnlyList<PolicyDecision>? Decisions = null, string? Approver = null);
public sealed record PolicyDecision(string Check, string Outcome, string RuleId, string Detail);
public sealed record QueueItem(Guid ItemId, Guid TenantId, string Kind, string SubjectRef, string PayloadRef, string State = Outcomes.AwaitingApproval, string? ClaimedBy = null, string? DecidedBy = null, string? Decision = null, DateTimeOffset? DecidedAt = null, string? Rationale = null, string? Purpose = null);
public sealed record ExecutionRecord(Guid ExecutionId, Guid ProposalId, string Outcome, DateTimeOffset AttemptedAt, string TargetResponseRef, string Approver);

public sealed class Catalog
{
    private readonly Dictionary<string, ActionType> _items;
    private Catalog(IEnumerable<ActionType> items) => _items = items.ToDictionary(x => x.ActionId, StringComparer.Ordinal);
    public ActionType Get(string actionId) => _items.TryGetValue(actionId, out var value) ? value : throw new InvalidOperationException("Unknown source-controlled action type.");
    public void AddAtRuntime(ActionType _) => throw new NotSupportedException("REQ-ACT-06: action types are source-controlled.");
    public static Catalog Load(string catalogDirectory, string vocabularyPath)
    {
        var purposes = File.ReadLines(vocabularyPath).Where(x => Regex.IsMatch(x, @"^  - [a-z-]+$")).Select(x => x.Trim()[2..]).ToHashSet();
        var types = Directory.GetFiles(catalogDirectory, "*.yaml").Select(path => Parse(File.ReadAllLines(path), purposes));
        return new Catalog(types);
    }
    private static ActionType Parse(string[] lines, HashSet<string> purposes)
    {
        string Value(string name) => lines.First(x => x.StartsWith(name + ":", StringComparison.Ordinal)).Split(':', 2)[1].Trim();
        var id = Value("action_id");
        if (!Regex.IsMatch(id, "^[a-z0-9_.]+@[0-9]+$")) throw new InvalidOperationException("Invalid action_id.");
        var listed = Regex.Match(Value("required_purpose"), @"\[(.*)\]").Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).ToHashSet();
        if (listed.Count == 0 || !listed.All(purposes.Contains)) throw new InvalidOperationException("Catalog purpose is not vendored vocabulary.");
        var max = int.Parse(lines.First(x => x.TrimStart().StartsWith("limit:")).Split(':')[1].Trim());
        var seconds = int.Parse(lines.First(x => x.TrimStart().StartsWith("window_seconds:")).Split(':')[1].Trim());
        return new(id, Value("target_system"), listed, Value("required_approval"), bool.Parse(Value("reversible")), max, TimeSpan.FromSeconds(seconds));
    }
}

public sealed class PolicyEngine
{
    private readonly Dictionary<(string Principal, string Action), Queue<DateTimeOffset>> _attempts = new();
    public IReadOnlyList<PolicyDecision> Evaluate(Proposal proposal, ActionType action, DateTimeOffset now)
    {
        var result = new List<PolicyDecision>();
        result.Add(new("purpose_entitlement", action.RequiredPurposes.Contains(proposal.Purpose) ? "allowed" : "refused", "PURPOSE-001", "Purpose is or is not permitted for this action type."));
        var delegation = Delegation(proposal.Principal, proposal.Purpose);
        result.Add(new("principal_delegation", delegation ? "allowed" : "refused", "DELEGATION-001", "Effective grant is the intersection of actor and delegator grants."));
        var key = (proposal.Principal.Id, action.ActionId);
        if (!_attempts.TryGetValue(key, out var attempts)) _attempts[key] = attempts = new();
        while (attempts.TryPeek(out var point) && point <= now - action.RateWindow) attempts.Dequeue();
        var allowed = attempts.Count < action.RateLimit;
        if (allowed) attempts.Enqueue(now);
        result.Add(new("rate_limit", allowed ? "allowed" : "refused", "RATE-001", "Rate assessed per principal, action and window."));
        return result;
    }
    private static bool Delegation(Principal principal, string purpose)
    {
        if (principal.Type != "agent") return principal.Grants?.Contains(purpose) ?? true;
        if (principal.OnBehalfOf is null || principal.OnBehalfOf.Type == "agent") return false;
        return (principal.Grants?.Contains(purpose) ?? false) && (principal.OnBehalfOf.Grants?.Contains(purpose) ?? false);
    }
}

public sealed class ReviewQueueService
{
    private readonly List<QueueItem> _items = [];
    public void Add(QueueItem item) => _items.Add(item);
    public IReadOnlyList<QueueItem> List(Guid tenantId) => _items.Where(x => x.TenantId == tenantId).ToList();
    // Mirrors the database RLS layer for the application-predicate removal test.
    public IReadOnlyList<QueueItem> ListWithDatabaseRls(Guid sessionTenant) => _items.Where(x => x.TenantId == sessionTenant).ToList();
    public QueueItem Claim(Guid tenantId, Guid itemId, string reviewer) => Update(tenantId, itemId, x => x with { ClaimedBy = reviewer });
    public QueueItem Decide(Guid tenantId, Guid itemId, string reviewer, string decision, string rationale, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(rationale) || decision is not (Outcomes.Approved or Outcomes.Rejected)) throw new ArgumentException("A decision needs reviewer, outcome and rationale.");
        return Update(tenantId, itemId, x => x with { State = decision, DecidedBy = reviewer, Decision = decision, DecidedAt = now, Rationale = rationale });
    }
    public IReadOnlyList<QueueItem> Audit(Guid tenantId) => List(tenantId).Where(x => x.Decision is not null).ToList();
    private QueueItem Update(Guid tenantId, Guid itemId, Func<QueueItem, QueueItem> change) { var index = _items.FindIndex(x => x.TenantId == tenantId && x.ItemId == itemId); if (index < 0) throw new KeyNotFoundException(); return _items[index] = change(_items[index]); }
}

public interface ISimulatedActionExecutor { ExecutionRecord Execute(Proposal proposal, string approver); }
// Real executors require a signed divisional write agreement and a scoped credential; see charter §3.3.
public sealed class SimulatedActionExecutor(string jsonlPath) : ISimulatedActionExecutor
{
    private readonly Dictionary<Guid, ExecutionRecord> _executions = [];
    public ExecutionRecord Execute(Proposal proposal, string approver)
    {
        if (_executions.TryGetValue(proposal.ProposalId, out var prior)) return prior;
        var result = new ExecutionRecord(Guid.NewGuid(), proposal.ProposalId, Outcomes.Executed, DateTimeOffset.UtcNow, $"simulated://{proposal.ProposalId}", approver);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);
        File.AppendAllText(jsonlPath, JsonSerializer.Serialize(new { proposal_id = proposal.ProposalId, action_id = proposal.ActionId, would_send = true }) + Environment.NewLine);
        return _executions[proposal.ProposalId] = result;
    }
}

public sealed class ActionBroker(Catalog catalog, PolicyEngine policy, ReviewQueueService queue, ISimulatedActionExecutor executor)
{
    private readonly Dictionary<Guid, Proposal> _proposals = [];
    public Proposal Submit(Proposal proposal, DateTimeOffset now)
    {
        var action = catalog.Get(proposal.ActionId);
        var decisions = policy.Evaluate(proposal, action, now);
        var accepted = decisions.All(x => x.Outcome == "allowed");
        var saved = proposal with { State = accepted ? Outcomes.AwaitingApproval : Outcomes.RefusedPolicy, Decisions = decisions };
        _proposals[saved.ProposalId] = saved;
        if (accepted) queue.Add(new(Guid.NewGuid(), saved.TenantId, "action_approval", saved.ProposalId.ToString(), $"proposal://{saved.ProposalId}", Purpose: saved.Purpose));
        return saved;
    }
    public Proposal Approve(Guid tenantId, Guid proposalId, string approver, string rationale, DateTimeOffset now)
    {
        var proposal = Get(tenantId, proposalId); if (proposal.ExpiresAt <= now) return _proposals[proposalId] = proposal with { State = Outcomes.Expired };
        var item = queue.List(tenantId).Single(x => x.SubjectRef == proposalId.ToString()); queue.Decide(tenantId, item.ItemId, approver, Outcomes.Approved, rationale, now);
        return _proposals[proposalId] = proposal with { State = Outcomes.Approved, Approver = approver };
    }
    public ExecutionRecord Execute(Guid tenantId, Guid proposalId, DateTimeOffset now)
    {
        var proposal = Get(tenantId, proposalId); if (proposal.ExpiresAt <= now) { _proposals[proposalId] = proposal with { State = Outcomes.Expired }; throw new InvalidOperationException("Proposal expired."); }
        if (proposal.State is not (Outcomes.Approved or Outcomes.Executed) || proposal.Approver is null) throw new InvalidOperationException("Proposal is not approved.");
        var execution = executor.Execute(proposal, proposal.Approver); _proposals[proposalId] = proposal with { State = execution.Outcome }; return execution;
    }
    public Proposal Get(Guid tenantId, Guid proposalId) => _proposals.TryGetValue(proposalId, out var proposal) && proposal.TenantId == tenantId ? proposal : throw new KeyNotFoundException();
}
