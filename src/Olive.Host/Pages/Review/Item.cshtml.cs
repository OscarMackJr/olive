using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Olive.Application;
using Olive.Contracts;
using Olive.Host.Auth;
using Olive.Host.Endpoints;
using Olive.Host.Pages.Shared;

namespace Olive.Host.Pages.Review;

public sealed record ParameterRow(string Name, string Value, bool Nested);

// UI-03, UI-04: claim, release and decide. All business logic stays in QueueService (Phase 5 D-01); the page feeds it a fresh
// ReviewerContext at decide time (D-10). Every POST ends in a redirect with a static flash code (D-11, D-19), except the role refusal.
public sealed class ItemModel(
    QueueService queue,
    IDecisionFreshness freshness,
    ExecutionService execution,
    IQueueKindRegistry registry,
    IProposalStore proposals,
    ILoggerFactory loggerFactory,
    DraftStore drafts,
    TimeProvider time) : ReviewerPageModel
{
    private const string FlashKey = "flash";
    private const string ProposalRefPrefix = "olive:proposal/";

    public bool ItemNotFound { get; private set; }

    public QueueItemView? Item { get; private set; }

    public ReviewerPayload? Payload { get; private set; }

    public bool PayloadHidden { get; private set; }

    public IReadOnlyList<ParameterRow> Parameters { get; private set; } = [];

    public FlashMessage? Flash { get; private set; }

    public bool FixtureTrace { get; private set; }

    public DateTimeOffset Now { get; private set; }

    public bool IsClaimant { get; private set; }

    public bool DecisionsSupported { get; private set; }

    public bool Expired { get; private set; }

    public bool DraftRestored { get; private set; }

    public string? DraftDecision { get; private set; }

    public string? DraftRationale { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!TryGetReviewer()) { return Page(); }

        Now = time.GetUtcNow();
        var item = await queue.GetAsync(Reviewer!, id, ct);
        if (item is null) { return NotFoundPage(); }

        var payload = await queue.GetPayloadAsync(Reviewer!, id, ct);
        switch (payload)
        {
            case PayloadResult.Found found:
                Payload = found.Payload;
                Parameters = ParseParameters(found.Payload.ParametersJson);
                break;
            case PayloadResult.RoleNotPermitted:
                PayloadHidden = true;
                break;
            case PayloadResult.NotFound:
                return NotFoundPage();
            default:
                throw new InvalidOperationException("Unknown payload result.");
        }

        Item = item;
        IsClaimant = item.State == "claimed" && item.ClaimedBy is not null && item.ClaimedBy == Reviewer!.Oid;
        Expired = item.ExpiresAt is { } expiry && expiry <= Now && item.State is "open" or "claimed";
        DecisionsSupported = registry.TryGet(item.Kind, out var kind) && kind.Decisions.SequenceEqual(["approve", "reject"], StringComparer.Ordinal);
        FixtureTrace = await IsFixtureTraceAsync(item, ct);
        Flash = FlashMessages.Resolve(TempData[FlashKey] as string);

        // D-05: restoring a draft only prefills the form. The reviewer confirms with a normal antiforgery POST; nothing is decided here.
        // The key is read from the query directly so it is never a bound handler argument.
        if (Request.Query.TryGetValue("resume", out var resume) && resume.Count == 1 && !string.IsNullOrEmpty(resume[0]))
        {
            var draft = IsClaimant ? drafts.Take(resume[0]!, Reviewer!.Oid, id) : null;
            if (draft is null)
            {
                Flash = FlashMessages.Resolve("draft_unavailable");
            }
            else
            {
                DraftDecision = draft.Decision;
                DraftRationale = draft.Rationale;
                DraftRestored = true;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostClaimAsync(Guid id, CancellationToken ct)
    {
        if (!TryGetReviewer()) { return Page(); }

        var result = await queue.ClaimAsync(Reviewer!, id, ct);
        var code = result switch
        {
            ClaimResult.Claimed => "claimed",
            ClaimResult.AlreadyClaimed => "already_claimed",
            ClaimResult.ProposalExpired => "proposal_expired",
            ClaimResult.Closed => "item_closed",
            ClaimResult.NotFound => null,
            _ => throw new InvalidOperationException("Unknown claim result."),
        };
        return code is null ? NotFoundPage() : Prg(id, code);
    }

    public async Task<IActionResult> OnPostReleaseAsync(Guid id, CancellationToken ct)
    {
        if (!TryGetReviewer()) { return Page(); }

        var result = await queue.ReleaseAsync(Reviewer!, id, ct);
        var code = result switch
        {
            ReleaseResult.Released => "released",
            ReleaseResult.NotClaimant => "not_claimant",
            ReleaseResult.NotClaimed => "item_not_claimed",
            ReleaseResult.ProposalExpired => "proposal_expired",
            ReleaseResult.Closed => "item_closed",
            ReleaseResult.NotFound => null,
            _ => throw new InvalidOperationException("Unknown release result."),
        };
        return code is null ? NotFoundPage() : Prg(id, code);
    }

    public async Task<IActionResult> OnPostDecideAsync(Guid id, CancellationToken ct)
    {
        if (!TryGetReviewer()) { return Page(); }

        // The form fields are read directly, not bound as handler parameters: the framework logs handler argument values at
        // Debug level, which would put the rationale in the logs (D-19).
        var form = await Request.ReadFormAsync(ct);
        var decision = form.TryGetValue("decision", out var d) && d.Count == 1 ? d[0] : null;
        var rationale = form.TryGetValue("rationale", out var r) && r.Count == 1 ? r[0] : null;

        // D-10: the cookie roles are not trusted here; the identity is re-resolved. A failure is a refusal and decides nothing.
        var outcome = await freshness.ResolveAsync(User, ct);
        if (outcome is FreshnessOutcome.Stale)
        {
            // D-05, D-06: the Entra session is too old. Keep the draft server-side and re-authenticate; nothing is decided here.
            if (decision is not ("approve" or "reject") || string.IsNullOrWhiteSpace(rationale) || rationale.Length > 4000)
            {
                return Prg(id, "invalid_request");
            }

            var key = drafts.Put(Reviewer!.Oid, id, decision, rationale);
            var props = new AuthenticationProperties { RedirectUri = "/review/items/" + id + "?resume=" + key };
            props.SetParameter(OpenIdConnectParameterNames.Prompt, "login");
            props.SetParameter<TimeSpan?>(OpenIdConnectParameterNames.MaxAge, TimeSpan.Zero);
            props.Items[ReviewerOidc.ReauthItem] = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return Challenge(props, ReviewerOidc.Scheme);
        }

        var fresh = outcome switch
        {
            FreshnessOutcome.Fresh f => f,
            FreshnessOutcome.Denied => null,
            _ => throw new InvalidOperationException("Unknown freshness outcome."),
        };
        if (fresh is null) { return Prg(id, "approval_identity_unresolved"); }

        var result = await queue.DecideAsync(fresh.Reviewer, id, decision, rationale, ct);
        await ApprovalExecution.RunAsync(result, execution, loggerFactory);

        string? code;
        switch (result)
        {
            case DecisionResult.Decided: code = decision == "approve" ? "decided_approve" : "decided_reject"; break;
            case DecisionResult.AlreadyDecidedSame: code = "already_decided_same"; break;
            case DecisionResult.Conflict: code = "decision_conflict"; break;
            case DecisionResult.NotClaimant: code = "not_claimant"; break;
            case DecisionResult.NotClaimed: code = "item_not_claimed"; break;
            case DecisionResult.Closed: code = "item_closed"; break;
            case DecisionResult.ProposalExpired: code = "proposal_expired"; break;
            case DecisionResult.NotFound: return NotFoundPage();
            case DecisionResult.SelfApproval: code = "self_approval_forbidden"; break;
            case DecisionResult.Unresolved: code = "approval_identity_unresolved"; break;
            case DecisionResult.Unavailable: code = "identity_resolution_unavailable"; break;
            case DecisionResult.KindNotSupported: code = "kind_not_supported"; break;
            case DecisionResult.InvalidRequest: code = "invalid_request"; break;
            case DecisionResult.RoleNotPermitted:
                // The one deliberate non-PRG POST (D-09): a refusal with no state change and no echoed input. No event is written.
                Forbid403(ProblemReasons.RoleNotHeld, id);
                return Page();
            default: throw new InvalidOperationException("Unknown decision result.");
        }

        return Prg(id, code);
    }

    private IActionResult Prg(Guid id, string code)
    {
        TempData[FlashKey] = code;
        return RedirectToPage(new { id });
    }

    private IActionResult NotFoundPage()
    {
        // D-19: the same page for a missing item and another tenant's item; it carries no item data.
        ItemNotFound = true;
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    private async Task<bool> IsFixtureTraceAsync(QueueItemView item, CancellationToken ct)
    {
        if (!item.PayloadRef.StartsWith(ProposalRefPrefix, StringComparison.Ordinal)
            || !Guid.TryParse(item.PayloadRef.AsSpan(ProposalRefPrefix.Length), out var proposalId))
        {
            return false;
        }

        var proposal = await proposals.GetAsync(item.TenantId, proposalId, ct);
        return proposal?.TraceVerification == "fixture";
    }

    private static IReadOnlyList<ParameterRow> ParseParameters(string? json)
    {
        if (string.IsNullOrEmpty(json)) { return []; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return []; }
            return doc.RootElement.EnumerateObject().Select(p => p.Value.ValueKind switch
            {
                JsonValueKind.String => new ParameterRow(p.Name, p.Value.GetString() ?? "", false),
                JsonValueKind.Object or JsonValueKind.Array => new ParameterRow(p.Name, JsonSerializer.Serialize(p.Value), true),
                _ => new ParameterRow(p.Name, p.Value.GetRawText(), false),
            }).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
