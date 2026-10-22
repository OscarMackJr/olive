using Olive.Application;
using Olive.Integrations.Hometown;

namespace Olive.Tests;

// Reads the committed contracts/trace-resolution cases through the production parser.
internal static class TraceFixtureLoader
{
    private static string Dir => Path.Combine(TestPaths.Root, "contracts", "trace-resolution");

    public static TraceFacts Load(string caseName)
    {
        var found = Assert.IsType<TraceLookup.Found>(TraceResolutionResponse.Parse(File.ReadAllText(Path.Combine(Dir, caseName + ".json"))));
        return found.Facts;
    }

    public static string LoadProposalBody(string caseName) => File.ReadAllText(Path.Combine(Dir, caseName + ".proposal.json"));

    public static string Seed(OliveApiFactory factory, string caseName)
    {
        var facts = Load(caseName);
        factory.FixtureResolver.Register(facts.TraceId, facts);
        return LoadProposalBody(caseName);
    }
}
