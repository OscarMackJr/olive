using System.Text.Json;
using Olive.Catalog;

namespace Olive.Tests;

public sealed class SchemaValidatorTests
{
    private const string Obj = """
        {"type":"object","additionalProperties":false,"required":["n"],
         "properties":{"n":{"type":"integer"},"id":{"type":"string","format":"uuid"}}}
        """;

    private static SchemaViolation? Check(string schema, string instance)
    {
        using var doc = JsonDocument.Parse(instance);
        return SchemaValidator.Compile(schema).Validate(doc.RootElement);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_valid_instance_returns_null() => Assert.Null(Check(Obj, """{"n":5}"""));

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_string_for_integer_fails_type_without_coercion()
    {
        var v = Check(Obj, """{"n":"5"}""");
        Assert.Equal(new SchemaViolation("/n", "type"), v);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_extra_property_reports_additionalProperties()
    {
        var v = Check(Obj, """{"n":5,"z":1}""");
        Assert.Equal(new SchemaViolation("/z", "additionalProperties"), v);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_format_assertion_is_on()
    {
        var v = Check(Obj, """{"n":5,"id":"not-a-uuid"}""");
        Assert.Equal(new SchemaViolation("/id", "format"), v);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_one_point_zero_satisfies_integer() => Assert.Null(Check(Obj, """{"n":1.0}"""));

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_dialect_is_forced_to_2020_12_prefixItems_honoured()
    {
        const string schema = """{"type":"array","prefixItems":[{"type":"integer"}]}""";
        Assert.Null(Check(schema, """[1]"""));
        Assert.Equal("/0", Check(schema, """["x"]""")?.Pointer);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_violation_never_exposes_the_instance_value()
    {
        const string secret = "sk-live-SECRET-VALUE-123";
        var v = Check(Obj, $$"""{"n":"{{secret}}"}""");
        Assert.NotNull(v);
        Assert.DoesNotContain(secret, v!.Pointer);
        Assert.DoesNotContain(secret, v.Keyword);
        Assert.DoesNotContain(secret, v.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_pointer_is_truncated_to_256_chars()
    {
        var name = new string('a', 400);
        var v = Check("""{"type":"object","additionalProperties":false}""", $$"""{"{{name}}":1}""");
        Assert.NotNull(v);
        Assert.Equal(256, v!.Pointer.Length);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_compiled_schema_is_thread_safe_across_parallel_validations()
    {
        var validator = SchemaValidator.Compile(Obj);
        using var good = JsonDocument.Parse("""{"n":5}""");
        using var bad = JsonDocument.Parse("""{"n":"5"}""");
        var results = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => i % 2 == 0 ? validator.Validate(good.RootElement) : validator.Validate(bad.RootElement)))
            .ToArray();
        Task.WaitAll(results);
        for (var i = 0; i < results.Length; i++)
        {
            if (i % 2 == 0)
            {
                Assert.Null(results[i].Result);
            }
            else
            {
                Assert.Equal(new SchemaViolation("/n", "type"), results[i].Result);
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_remote_ref_never_resolves_over_the_network()
    {
        const string schema = """{"$ref":"https://example.invalid/remote.schema.json"}""";
        using var doc = JsonDocument.Parse("""{"a":1}""");
        Assert.ThrowsAny<Exception>(() => SchemaValidator.Compile(schema).Validate(doc.RootElement));
    }
}
