using System.Text.Json;
using System.Text.Json.Nodes;
using Olive.Catalog;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class QueueSchemaTests
{
    private const string Dir = "contracts/queue-item/v1/";

    private static string Read(string name) => File.ReadAllText(Path.Combine(TestPaths.Root, Dir + name));

    private static SchemaViolation? Check(string instance)
    {
        var validator = SchemaValidator.Compile(Read("queue-item.schema.json"));
        using var doc = JsonDocument.Parse(instance);
        return validator.Validate(doc.RootElement);
    }

    private static string WithPayloadRef(string value)
    {
        var node = JsonNode.Parse(Read("queue-item.valid.json"))!.AsObject();
        node["payload_ref"] = value;
        return node.ToJsonString();
    }

    [Fact]
    public void QUE_01_valid_example_validates() => Assert.Null(Check(Read("queue-item.valid.json")));

    [Fact]
    public void QUE_01_unknown_property_is_rejected() => Assert.NotNull(Check(Read("queue-item.invalid.json")));

    [Fact]
    public void QUE_01_inline_payload_example_is_rejected()
    {
        var v = Check(Read("queue-item.inline-payload.invalid.json"));
        Assert.NotNull(v);
        Assert.Equal("/payload_ref", v!.Pointer);
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("payload://abc")]
    [InlineData("this is a long free text note about the customer that must never be carried in a queue item payload reference")]
    [InlineData("olive:proposal/not-a-uuid")]
    public void QUE_01_payload_ref_that_is_not_a_reference_is_rejected(string value) =>
        Assert.NotNull(Check(WithPayloadRef(value)));

    [Fact]
    public void QUE_01_v0_placeholder_contract_is_gone() =>
        Assert.False(Directory.Exists(Path.Combine(TestPaths.Root, "contracts", "queue-item", "v0")));
}
