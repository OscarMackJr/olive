using System.Text.Json;

namespace Olive.Catalog;

public abstract record SubmissionOutcome
{
    private SubmissionOutcome() { }

    public sealed record Ok(CatalogEntry Entry) : SubmissionOutcome;

    public sealed record Violation(SchemaViolation Detail) : SubmissionOutcome;

    public sealed record NotInCatalog : SubmissionOutcome;
}

// Order (D-13): 1 envelope schema, 2 current-release catalog lookup, 3 catalog parameter schema.
// The instance is validated exactly as parsed: no coercion, defaults, stripping or trimming (D-03).
public sealed class SubmissionValidator(CatalogSet catalog)
{
    private static readonly Lazy<SchemaValidator> Envelope = new(() =>
    {
        using var s = typeof(SubmissionValidator).Assembly.GetManifestResourceStream("contracts/proposal/v1/proposal-request.schema.json")
            ?? throw new InvalidOperationException("Contract schema is missing.");
        using var r = new StreamReader(s);
        return SchemaValidator.Compile(r.ReadToEnd());
    });

    public SubmissionOutcome Validate(JsonElement root)
    {
        var envelope = Envelope.Value.Validate(root);
        if (envelope is not null)
        {
            return new SubmissionOutcome.Violation(envelope);
        }

        var entry = LookupAction(root.GetProperty("action_type").GetString()!);
        if (entry is null)
        {
            return new SubmissionOutcome.NotInCatalog();
        }

        var parameters = entry.ParametersSchema.Validate(root.GetProperty("parameters"));
        if (parameters is not null)
        {
            return new SubmissionOutcome.Violation(parameters with { Pointer = "/parameters" + parameters.Pointer });
        }

        return new SubmissionOutcome.Ok(entry);
    }

    // Phase 4 replaces this seam with a stored refused_policy not_in_catalog. Until then an unknown action is a non-stored 400.
    // Current release only: older releases are GET-only (D-13).
    private CatalogEntry? LookupAction(string actionType) =>
        catalog.TryFindAction(actionType, out var entry) ? entry : null;
}
