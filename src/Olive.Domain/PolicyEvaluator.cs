using System.Text.Json;

namespace Olive.Domain;

public sealed record PolicyCheck(string Name, string Result);

// Checks run in order; the first failure stops the chain and later checks are omitted (D-18, D-20).
// Then() lets later plans append entitlement and rate checks without changing the first-stage signature.
public sealed record PolicyVerdict(bool Allowed, string? Reason, IReadOnlyList<PolicyCheck> Checks)
{
    public PolicyVerdict Then(string checkName, bool passed, string failReason)
    {
        if (!Allowed)
        {
            return this;
        }

        return new PolicyVerdict(passed, passed ? null : failReason, [.. Checks, new PolicyCheck(checkName, passed ? PolicyEvaluator.Pass : PolicyEvaluator.Fail)]);
    }

    // Built only from static names and reason codes: never parameter values, on_behalf_of or group lists (D-20).
    public string ToDetailJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("outcome", Allowed ? "allowed" : "refused");
            if (Reason is null)
            {
                w.WriteNull("reason");
            }
            else
            {
                w.WriteString("reason", Reason);
            }

            w.WriteStartArray("checks");
            foreach (var c in Checks)
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteString("result", c.Result);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

public static class PolicyEvaluator
{
    public const string Catalog = "catalog";
    public const string Purpose = "purpose";
    public const string Entitlement = "entitlement";
    public const string Rate = "rate";
    public const string Pass = "pass";
    public const string Fail = "fail";

    // Pure and primitive-only: Domain cannot reference Olive.Catalog. Ordinal, case-sensitive; an empty list fails closed.
    public static PolicyVerdict EvaluateCatalogAndPurpose(bool actionKnown, IReadOnlyCollection<string>? requiredPurposes, string purpose)
    {
        var verdict = new PolicyVerdict(true, null, []).Then(Catalog, actionKnown, PolicyRefusalReasons.NotInCatalog);
        var permitted = requiredPurposes is { Count: > 0 } && requiredPurposes.Any(p => string.Equals(p, purpose, StringComparison.Ordinal));
        return verdict.Then(Purpose, permitted, PolicyRefusalReasons.PurposeNotPermitted);
    }
}
