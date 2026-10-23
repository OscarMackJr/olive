using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Olive.Application;

namespace Olive.Integrations.Entra;

// Fixture-backed dev reviewer identities (D-07). Non-production only (FND-05). Lookup is ordinal on the reviewer id.
public sealed class FixtureReviewerIdentitySource : IReviewerIdentitySource, INonProductionOnly
{
    private readonly ConcurrentDictionary<string, ReviewerIdentity> _identities = new(StringComparer.Ordinal);

    public static FixtureReviewerIdentitySource CreateDefault()
    {
        var source = new FixtureReviewerIdentitySource();
        var asm = typeof(FixtureReviewerIdentitySource).Assembly;
        var files = JsonSerializer.Deserialize<string[]>(ReadResource(asm, "reviewer/index.json"))
            ?? throw new InvalidOperationException("Reviewer fixture index is malformed.");
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(ReadResource(asm, "reviewer/" + file));
            var root = doc.RootElement;
            var groups = root.GetProperty("groups").EnumerateArray().Select(g => g.GetString() ?? throw new InvalidOperationException("A reviewer fixture is malformed.")).ToArray();
            var identity = new ReviewerIdentity(
                root.GetProperty("reviewer_id").GetString()!, root.GetProperty("oid").GetString()!,
                root.GetProperty("tid").GetString()!, root.GetProperty("upn").GetString()!, groups);
            source._identities[identity.ReviewerId] = identity;
        }

        return source;
    }

    public ReviewerIdentity? Find(string reviewerId) => _identities.TryGetValue(reviewerId, out var identity) ? identity : null;

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException("A reviewer fixture resource is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
