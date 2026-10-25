using Olive.Application;
using Olive.Domain;

namespace Olive.Host.Pages.Review;

// UI-02: the reviewer's own-tenant open and claimed items, IR-1 list fields only. Data access goes through QueueService alone.
public sealed class IndexModel(QueueService queue, TimeProvider time) : ReviewerPageModel
{
    public IReadOnlyList<QueueItemView> Items { get; private set; } = [];

    public DateTimeOffset Now { get; private set; }

    public bool LimitReached => Items.Count >= QueueService.DefaultLimit;

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!TryGetReviewer()) { return; }

        Now = time.GetUtcNow();
        // UI-SPEC /review: the State badge shows open and claimed. Each list is tenant-filtered and excludes expired items;
        // merging the oldest page of each and re-taking the oldest page keeps the store's (created_at, item_id) order.
        var open = await queue.ListAsync(Reviewer!, null, QueueStates.Open, null, ct);
        var claimed = await queue.ListAsync(Reviewer!, null, QueueStates.Claimed, null, ct);
        Items = (open, claimed) switch
        {
            (QueueListResult.Ok o, QueueListResult.Ok c) => o.Items.Concat(c.Items)
                .OrderBy(i => i.CreatedAt).ThenBy(i => i.ItemId)
                .Take(QueueService.DefaultLimit).ToList(),
            _ => throw new InvalidOperationException("Unknown list result."),
        };
    }
}
