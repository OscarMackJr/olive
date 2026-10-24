namespace Olive.Application;

// D-05: the catalog is re-checked at execution time. Purpose and entitlement are not re-checked (Q16).
public interface IActionCatalogCheck
{
    CatalogCheckResult Check(string actionId, string parametersJson);
}

public sealed record CatalogCheckResult(bool Executable, string? TargetSystem);
