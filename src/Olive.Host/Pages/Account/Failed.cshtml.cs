using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Olive.Host.Pages.Account;

// D-03: static. Reads nothing from the request, so no error text can be echoed.
public sealed class FailedModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status400BadRequest;
}
