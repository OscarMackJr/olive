using System.Text.Json;
using Olive.Application;

namespace Olive.Catalog;

// D-05 / Q16: executable only when the current release still has the action and its schema still accepts the stored parameters.
public sealed class CatalogExecutionCheck(CatalogSet catalog) : IActionCatalogCheck
{
    public CatalogCheckResult Check(string actionId, string parametersJson)
    {
        var none = new CatalogCheckResult(false, null);
        if (!catalog.TryFindAction(actionId, out var entry)) { return none; }

        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (entry.ParametersSchema.Validate(doc.RootElement) is not null) { return none; }
        }
        catch (JsonException)
        {
            return none;
        }

        string? target = null;
        if (entry.EntryJson.ValueKind == JsonValueKind.Object
            && entry.EntryJson.TryGetProperty("target_system", out var t) && t.ValueKind == JsonValueKind.String)
        {
            target = t.GetString();
        }

        return new CatalogCheckResult(true, target);
    }
}
