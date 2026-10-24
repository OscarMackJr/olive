using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;
using Olive.Host.Auth;

namespace Olive.Host.Pages.Dev;

// D-02: fixture reviewer picker. Exists only while DevSignInGate is registered, which FND-05 refuses in production.
// The form carries a fixture id only; identity, groups and tenant are re-resolved server-side exactly as the DevReviewer scheme does.
public sealed class SignInModel(IReviewerIdentitySource identities, IReviewerTenantMap tenantMap, TimeProvider time, DevSignInGate? gate = null) : PageModel
{
    public IReadOnlyList<ReviewerIdentity> Reviewers { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        if (gate is null) { return NotFound(); }
        Reviewers = identities.All();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? reviewerId)
    {
        if (gate is null) { return NotFound(); }
        var identity = string.IsNullOrEmpty(reviewerId) ? null : identities.Find(reviewerId);
        if (identity is null)
        {
            return RedirectToPage();
        }

        // D-08: the tenant claim exists only when the server-side map yields exactly one tenant.
        var tenants = tenantMap.TenantsFor(identity.Groups).Distinct().ToArray();
        Guid? tenant = tenants.Length == 1 ? tenants[0] : null;
        var principal = ReviewerClaims.WithSession(
            ReviewerClaims.ToPrincipal(identity, tenant, QueueStates.Stub, ReviewerCookie.Scheme),
            time.GetUtcNow(), identity.ReviewerId, null, tenant is null ? ProblemReasons.NoReviewerTenant : null);
        await ReviewerCookie.SignInAsync(HttpContext, principal);
        return !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : Redirect("/review");
    }
}
