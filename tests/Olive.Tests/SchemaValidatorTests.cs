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
        Assert.Equal(new SchemaViolation("", "additionalProperties"), v);
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
        var v = Check("{\"type\":\"object\",\"properties\":{\"" + name + "\":{\"type\":\"integer\"}}}", $$"""{"{{name}}":"x"}""");
        Assert.NotNull(v);
        Assert.Equal(256, v!.Pointer.Length);
    }

    // WR-03: an unknown key is caller-chosen text (it may be a pasted secret), so it is never echoed; the refusing object is.
    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_unknown_key_pointer_names_the_parent_not_the_key()
    {
        const string schema = """{"type":"object","additionalProperties":false,"properties":{"o":{"type":"object","additionalProperties":false}}}""";
        var v = Check(schema, """{"o":{"sk-live-SECRET-KEY":1}}""");
        Assert.Equal(new SchemaViolation("/o", "additionalProperties"), v);
        var u = Check("""{"type":"object","properties":{"a":{"type":"string"}},"unevaluatedProperties":false}""", """{"a":"x","sk-live-SECRET-KEY":1}""");
        Assert.Equal(new SchemaViolation("", "unevaluatedProperties"), u);
    }

    // WR-03: the cap applies to the final pointer and never leaves a lone high surrogate or a dangling `~` escape.
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(255, "😀", 255)]
    [InlineData(255, "~1", 255)]
    [InlineData(256, "z", 256)]
    public void API_05_pointer_cap_never_splits_a_surrogate_pair_or_escape(int prefixLength, string tail, int expectedLength)
    {
        var pointer = "/" + new string('a', prefixLength - 1) + tail;
        var capped = SchemaValidator.CapPointer(pointer);
        Assert.Equal(expectedLength, capped.Length);
        Assert.False(char.IsHighSurrogate(capped[^1]));
        Assert.NotEqual('~', capped[^1]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_prefixed_parameters_pointer_is_capped_after_prefixing()
    {
        var name = new string('a', 300);
        var schema = SchemaValidator.Compile("{\"type\":\"object\",\"properties\":{\"" + name + "\":{\"type\":\"integer\"}}}");
        var catalog = new CatalogSet([new CatalogRelease(1, [new CatalogEntry("crm.case_note@1", JsonDocument.Parse("{}").RootElement, schema)])]);
        using var doc = JsonDocument.Parse($$"""{"run_id":"r","action_type":"crm.case_note@1","party_ref":"p","parameters":{"{{name}}":"x"},"justification":"j","trace_ref":"t"}""");
        var v = Assert.IsType<SubmissionOutcome.Violation>(new SubmissionValidator(catalog).Validate(doc.RootElement));
        Assert.Equal(256, v.Detail.Pointer.Length);
        Assert.StartsWith("/parameters/", v.Detail.Pointer);
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
