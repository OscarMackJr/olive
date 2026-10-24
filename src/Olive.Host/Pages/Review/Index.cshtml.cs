using Olive.Application;

namespace Olive.Host.Pages.Review;

// UI-02: the reviewer's own-tenant open items, IR-1 list fields only. Data access goes through QueueService alone.
public sealed class IndexModel(QueueService queue, TimeProvider time) : ReviewerPageModel
{
    public IReadOnlyList<QueueItemView> Items { get; private set; } = [];

    public DateTimeOffset Now { get; private set; }

    public bool LimitReached => Items.Count >= QueueService.DefaultLimit;

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!TryGetReviewer()) { return; }

        Now = time.GetUtcNow();
        var result = await queue.ListAsync(Reviewer!, null, "open", null, ct);
        Items = result switch
        {
            QueueListResult.Ok ok => ok.Items,
            _ => throw new InvalidOperationException("Unknown list result."),
        };
    }
}
