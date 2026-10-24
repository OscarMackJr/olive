using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Olive.Domain;
using Olive.Host.Auth;

namespace Olive.Host.Pages.Account;

// POST only, with the built-in antiforgery check. Entra sessions also sign out at the IdP; stub sessions clear the cookie only.
public sealed class SignOutModel : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        if (User.FindFirst(ReviewerClaims.AuthSource)?.Value == QueueStates.Entra)
        {
            return SignOut(new AuthenticationProperties { RedirectUri = "/signed-out" }, ReviewerCookie.Scheme, ReviewerOidc.Scheme);
        }

        await HttpContext.SignOutAsync(ReviewerCookie.Scheme);
        return Redirect("/signed-out");
    }
}
