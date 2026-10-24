namespace Olive.Host.Pages.Shared;

public sealed record FlashMessage(string Kind, string Text)
{
    public bool IsError => Kind == FlashMessages.Error;
}

// D-19: TempData carries only a static code; this map turns it into static copy. Unknown codes resolve to null and render nothing.
public static class FlashMessages
{
    public const string Success = "success";
    public const string Error = "error";

    private static readonly Dictionary<string, FlashMessage> Map = new(StringComparer.Ordinal)
    {
        ["claimed"] = new(Success, "Done: you claimed this item. Only you can decide or release it now."),
        ["released"] = new(Success, "Done: you released this item. It is back in the queue."),
        ["decided_approve"] = new(Success, "Done: approved. The simulated execution result is in the audit history."),
        ["decided_reject"] = new(Success, "Done: rejected. The decision is recorded in the audit history."),
        ["already_decided_same"] = new(Success, "This item already has the same decision. Nothing was changed."),
        ["already_claimed"] = new(Error, "Error: another reviewer claimed this item first. Return to the queue and pick another item."),
        ["not_claimant"] = new(Error, "Error: only the reviewer who claimed this item can do that."),
        ["item_not_claimed"] = new(Error, "Error: claim this item before you decide it."),
        ["item_closed"] = new(Error, "Error: this item is already closed. See the audit history."),
        ["proposal_expired"] = new(Error, "Error: this proposal expired before it was decided. It can no longer be approved."),
        ["decision_conflict"] = new(Error, "Error: this item was already decided differently. See the audit history."),
        ["self_approval_forbidden"] = new(Error, "Error: you cannot approve a proposal made on your own behalf. Release the item so another reviewer can decide it."),
        ["approval_identity_unresolved"] = new(Error, "Error: your identity could not be confirmed against the proposal, so approval was refused. Release the item or try again later."),
        ["identity_resolution_unavailable"] = new(Error, "Error: the identity check is unavailable right now. Nothing was decided. Try again in a few minutes."),
        ["kind_not_supported"] = new(Error, "Error: decisions for this item kind are not supported."),
        ["invalid_request"] = new(Error, "Error: choose Approve or Reject and enter a rationale of up to 4000 characters."),
        ["draft_unavailable"] = new(Error, "Error: your draft decision could not be restored. Enter it again."),
        ["internal_error"] = new(Error, "Error: something went wrong and nothing was changed. Try again. If it keeps happening, contact the Olive operators."),
    };

    public static FlashMessage? Resolve(string? code) => code is not null && Map.TryGetValue(code, out var message) ? message : null;
}
