using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Olive.Host.Auth;

namespace Olive.Host.Pages.Account;

// UI-01: no markup. Challenges the Entra scheme; the return URL must be local (T-07-23).
public sealed class SignInModel(IAuthenticationSchemeProvider schemes) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        if (await schemes.GetSchemeAsync(ReviewerOidc.Scheme) is null)
        {
            return NotFound();
        }

        var target = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/review";
        return Challenge(new AuthenticationProperties { RedirectUri = target }, ReviewerOidc.Scheme);
    }
}
