using Microsoft.AspNetCore.Mvc.RazorPages;
using Olive.Application;
using Olive.Contracts;
using Olive.Host.Auth;

namespace Olive.Host.Pages.Review;

// Base for the /review pages. A session without exactly one tenant gets HTTP 403 with a static reason code (D-04).
public abstract class ReviewerPageModel : PageModel
{
    public ReviewerContext? Reviewer { get; private set; }

    public string? ForbiddenReason { get; protected set; }

    public Guid? ForbiddenItemId { get; protected set; }

    protected bool TryGetReviewer()
    {
        Reviewer = User.ToReviewerContext();
        if (Reviewer is not null) { return true; }

        var recorded = User.FindFirst(ReviewerClaims.NoTenantReason)?.Value;
        Forbid403(recorded is ProblemReasons.GroupsOverage ? ProblemReasons.GroupsOverage : ProblemReasons.NoReviewerTenant, null);
        return false;
    }

    protected void Forbid403(string reason, Guid? itemId)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        ForbiddenReason = reason;
        ForbiddenItemId = itemId;
    }
}
