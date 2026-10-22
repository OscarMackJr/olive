using System.Text.Json;
using Olive.Application;
using Olive.Catalog;
using Olive.Domain;
using Olive.Integrations.Hometown;

namespace Olive.Tests;

public sealed class TraceFixtureTests
{
    private static string Dir => Path.Combine(TestPaths.Root, "contracts", "trace-resolution");

    private static readonly SchemaValidator ResponseValidator =
        SchemaValidator.Compile(File.ReadAllText(Path.Combine(Dir, "trace-resolution-response.schema.json")));

    private static readonly SchemaValidator RequestValidator =
        SchemaValidator.Compile(File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "proposal", "v1", "proposal-request.schema.json")));

    private static readonly string[] Cases =
        ["match", "run-null-ate03", "run-differs", "tenant-differs", "agent-null", "agent-differs", "hash-null", "hash-differs"];

    private static JsonElement Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, file))).RootElement.Clone();

    private static ServiceTokenGrant Grant() =>
        new(new ServiceToken("t"), TestPaths.TenantAlpha, "liam", "0a1b2c3d-0000-4000-8000-000000000001", DateTimeOffset.UtcNow.AddMinutes(5));

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_01_index_lists_exactly_the_eight_response_files()
    {
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Dir, "index.json")))!;
        Assert.Equal(8, index.Count);
        Assert.DoesNotContain(index.Values, v => v.Contains(".proposal.") || v.Contains(".schema."));
        Assert.Equal(8, Directory.GetFiles(Dir, "*.proposal.json").Length);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_01_every_fixture_response_validates_against_the_schema()
    {
        foreach (var c in Cases)
        {
            Assert.Null(ResponseValidator.Validate(Load(c + ".json")));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_01_invalid_example_with_eighth_property_fails()
    {
        Assert.NotNull(ResponseValidator.Validate(Load("trace-resolution-response.invalid.json")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_01_every_proposal_file_validates_and_binds_its_trace_ref_and_run_id()
    {
        foreach (var c in Cases)
        {
            var p = Load(c + ".proposal.json");
            Assert.Null(RequestValidator.Validate(p));
            Assert.Equal("trace://fixture/" + c, p.GetProperty("trace_ref").GetString());
            Assert.Equal("run-fixture-" + c, p.GetProperty("run_id").GetString());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_06_fixture_hash_equals_production_proposal_hash_for_every_pair_except_hash_mismatch_cases()
    {
        foreach (var c in Cases)
        {
            var hash = Load(c + ".json").GetProperty("proposal_hash");
            var actual = Jcs.ProposalHash(Load(c + ".proposal.json"));
            switch (c)
            {
                case "hash-null":
                    Assert.Equal(JsonValueKind.Null, hash.ValueKind);
                    break;
                case "hash-differs":
                    Assert.NotEqual(actual, hash.GetString());
                    break;
                default:
                    Assert.Equal(actual, hash.GetString());
                    break;
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_01_resolver_finds_each_indexed_ref_and_not_found_for_unknown()
    {
        var resolver = FixtureTraceResolver.CreateDefault();
        foreach (var c in Cases)
        {
            var r = await resolver.ResolveAsync(Grant(), "trace://fixture/" + c, CancellationToken.None);
            var found = Assert.IsType<TraceLookup.Found>(r);
            Assert.Equal(TraceVerificationSources.Fixture, found.Source);
        }

        Assert.IsType<TraceLookup.NotFound>(await resolver.ResolveAsync(Grant(), "trace://fixture/unknown", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_01_resolver_returns_tenant_differs_fixture_verbatim_without_tenant_scoping()
    {
        var r = await FixtureTraceResolver.CreateDefault().ResolveAsync(Grant(), "trace://fixture/tenant-differs", CancellationToken.None);
        Assert.Equal(TestPaths.TenantBeta, Assert.IsType<TraceLookup.Found>(r).Facts.TenantId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TRC_01_register_replaces_the_entry()
    {
        var resolver = new FixtureTraceResolver();
        var facts = new TraceFacts("x", "ate/0.4", "r", TestPaths.TenantAlpha, "liam", null, DateTimeOffset.UnixEpoch);
        resolver.Register("x", facts);
        resolver.Register("x", facts with { RunId = "r2" });
        Assert.Equal("r2", Assert.IsType<TraceLookup.Found>(await resolver.ResolveAsync(Grant(), "x", CancellationToken.None)).Facts.RunId);
    }

    private static string Good => File.ReadAllText(Path.Combine(Dir, "match.json"));

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_01_parser_accepts_a_good_response()
    {
        Assert.IsType<TraceLookup.Found>(TraceResolutionResponse.Parse(Good));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_01_parser_returns_malformed_for_extra_missing_bad_version_and_uppercase_tenant()
    {
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse(Good.Replace("\"created_at\"", "\"extra\": 1, \"created_at\"")));
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse(Good.Replace("\"agent_id\": \"liam\",", "")));
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse(Good.Replace("ate/0.4", "v0.4")));
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse(Good.Replace("5325ffe9-5727-4d88-9832-aebabb8d0538", "5325FFE9-5727-4D88-9832-AEBABB8D0538")));
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse("[]"));
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse("not json"));
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse(Good.Replace("\"liam\"", "\"\"")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_07_parser_returns_malformed_for_duplicate_keys_and_never_throws()
    {
        // Seven properties with trace_id twice and created_at missing: the count matches, so only a uniqueness check catches it.
        var dupInsteadOfCreated = Good.Replace("\"created_at\"", "\"trace_id\"");
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse(dupInsteadOfCreated));

        // A duplicate on top of a complete set (eight properties) is malformed too.
        var dupExtra = Good.Replace("\"agent_id\": \"liam\",", "\"agent_id\": \"liam\", \"agent_id\": \"mallory\",");
        Assert.NotEqual(Good, dupExtra);
        Assert.IsType<TraceLookup.Malformed>(TraceResolutionResponse.Parse(dupExtra));
    }
}
