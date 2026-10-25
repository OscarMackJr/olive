using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Olive.Application;

namespace Olive.Host.Pages.Review;

// UI-05 / D-12..D-14: read-only audit page. Data access goes through QueueService and IAuditStore only; no new endpoint.
public sealed class AuditModel(QueueService queue, IAuditStore audit) : ReviewerPageModel
{
    public Guid ItemId { get; private set; }

    public QueueItemView Item { get; private set; } = null!;

    public AuditRecord? Record { get; private set; }

    public bool CanSeeFields { get; private set; }

    public bool HashMatches { get; private set; }

    public bool ShowChain { get; private set; }

    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; private set; } = [];

    public IReadOnlyList<QueueItemEventRecord> Events { get; private set; } = [];

    public IReadOnlyList<KeyValuePair<long, string>> DeferRationales { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        if (!TryGetReviewer()) { return Page(); }

        var item = await queue.GetAsync(Reviewer!, id, ct);
        if (item is null) { return NotFound(); }
        Item = item;
        ItemId = id;

        if (item.ProposalId is { } proposalId)
        {
            Record = await audit.GetAsync(Reviewer!.TenantId, proposalId, ct);
        }

        var payload = await queue.GetPayloadAsync(Reviewer!, id, ct);
        switch (payload)
        {
            case PayloadResult.Found: CanSeeFields = true; break;
            case PayloadResult.RoleNotPermitted: CanSeeFields = false; break;
            case PayloadResult.NoPayload: CanSeeFields = true; break;
            case PayloadResult.NotFound: return NotFound();
            default: throw new InvalidOperationException("Unknown payload result.");
        }

        if (Record is not null)
        {
            HashMatches = AuditHashCheck.Matches(Record);
            if (CanSeeFields) { Parameters = ParseParameters(Record.ParametersJson); }
        }

        ShowChain = Record is not null && item.State == "decided" && item.Decision == "approve";

        var events = await queue.EventsAsync(Reviewer!, id, ct);
        switch (events)
        {
            case EventsResult.Found found:
                Events = found.Events.OrderBy(e => e.Seq).ToList();
                // The service already strips the rationale for reviewers without the kind role; it is shown only when they hold it.
                if (CanSeeFields)
                {
                    DeferRationales = Events.Where(e => e.EventType == "deferred").Select(e => new KeyValuePair<long, string>(e.Seq, ReadRationale(e.DetailJson)))
                        .Where(p => p.Value.Length > 0).ToList();
                }
                break;
            case EventsResult.NotFound: return NotFound();
            default: throw new InvalidOperationException("Unknown events result.");
        }

        return Page();
    }

    private static string ReadRationale(string? json)
    {
        if (string.IsNullOrEmpty(json)) { return ""; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    // Top-level keys only; nested values are shown as compact JSON text. Malformed stored text yields no rows (fail closed).
    private static List<KeyValuePair<string, string>> ParseParameters(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return []; }
            return doc.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
