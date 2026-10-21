using System.Text.Json;

namespace Olive.Tests;

public sealed class IntrospectionFixtureTests
{
    private static string Dir => Path.Combine(TestPaths.Root, "contracts", "introspection");

    private static JsonElement Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, file))).RootElement.Clone();

    private static JsonElement Schema => Load("introspection-response.schema.json");

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_all_introspection_fixtures_validate_against_schema()
    {
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Dir, "index.json")))!;
        Assert.Equal(6, index.Count);
        foreach (var file in index.Values)
        {
            Assert.Empty(JsonSchemaLite.Validate(Schema, Load(file)));
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_inactive_fixture_carries_only_active_false()
    {
        var f = Load("inactive.json");
        Assert.Single(f.EnumerateObject());
        Assert.False(f.GetProperty("active").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_inactive_fixture_with_extra_property_is_invalid()
    {
        using var doc = JsonDocument.Parse("""{"active": false, "key_id": "x"}""");
        Assert.NotEmpty(JsonSchemaLite.Validate(Schema, doc.RootElement));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_active_fixture_with_uppercase_key_id_is_invalid()
    {
        var json = File.ReadAllText(Path.Combine(Dir, "liam-alpha.json")).Replace("0a1b2c3d-0000-4000-8000-000000000001", "0A1B2C3D-0000-4000-8000-000000000001");
        using var doc = JsonDocument.Parse(json);
        Assert.NotEmpty(JsonSchemaLite.Validate(Schema, doc.RootElement));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_active_fixture_with_service_principal_is_invalid()
    {
        var json = File.ReadAllText(Path.Combine(Dir, "liam-alpha.json")).Replace("\"agent\"", "\"service\"");
        using var doc = JsonDocument.Parse(json);
        Assert.NotEmpty(JsonSchemaLite.Validate(Schema, doc.RootElement));
    }
}
