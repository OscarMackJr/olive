namespace Olive.Host.Pages.Shared;

// Static reason code only (D-04); never carries claim values or request data.
public sealed record ForbiddenViewModel(string Reason, Guid? ItemId);
