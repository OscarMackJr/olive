using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Olive.Host.Auth;

namespace Olive.Host.Pages.Account;

// POST only, with the built-in antiforgery check. Plan 04 adds the IdP sign-out branch for Entra sessions.
public sealed class SignOutModel : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(ReviewerCookie.Scheme);
        return Redirect("/signed-out");
    }
}
