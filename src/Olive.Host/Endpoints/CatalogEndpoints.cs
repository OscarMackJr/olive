using System.Globalization;
using Olive.Catalog;
using Olive.Contracts;
using Olive.Host.Problems;

namespace Olive.Host.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // D-11: any active key may read the catalog (not tenant data); no principal-type or allowlist check.
        app.MapGet("/v1/catalog/{version}", GetAsync).RequireAuthorization();
        return app;
    }

    private static IResult GetAsync(string version, CatalogSet catalog)
    {
        // D-12: unparseable and unknown versions are the same 404.
        if (!IsDigits(version)
            || !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !catalog.TryGetRelease(number, out var release))
        {
            return ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.CatalogVersionNotFound);
        }

        // Entries are written from the stored JsonElements so numbers and booleans pass through unchanged.
        return Results.Json(new { catalog_version = release.Version, entries = release.Entries.Select(e => e.EntryJson).ToList() });
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');
}
