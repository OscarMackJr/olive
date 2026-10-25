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

    private static SchemaViolation? CheckCreate(string instance)
    {
        var validator = SchemaValidator.Compile(Read("identity-create.schema.json"));
        using var doc = JsonDocument.Parse(instance);
        return validator.Validate(doc.RootElement);
    }

    private static string CreateWith(string property, JsonNode? value)
    {
        var node = JsonNode.Parse(Read("identity-create.valid.json"))!.AsObject();
        if (value is null) { node.Remove(property); } else { node[property] = value; }
        return node.ToJsonString();
    }

    [Fact]
    public void IRQ_01_create_valid_example_validates() => Assert.Null(CheckCreate(Read("identity-create.valid.json")));

    [Theory]
    [InlineData("identity-create.invalid.json")]
    [InlineData("identity-create.link-challenge.invalid.json")]
    [InlineData("identity-create.server-assigned.invalid.json")]
    public void IRQ_01_create_invalid_examples_are_rejected(string file) => Assert.NotNull(CheckCreate(Read(file)));

    [Fact]
    public void IRQ_01_create_missing_field_is_rejected() => Assert.NotNull(CheckCreate(CreateWith("ruleset_version", null)));

    [Fact]
    public void IRQ_01_create_non_uuid_producer_ref_is_rejected() => Assert.NotNull(CheckCreate(CreateWith("producer_ref", "not-a-uuid")));

    [Fact]
    public void IRQ_01_create_ruleset_version_over_128_chars_is_rejected() => Assert.NotNull(CheckCreate(CreateWith("ruleset_version", new string('a', 129))));

    [Fact]
    public void IRQ_01_create_ruleset_version_with_spaces_is_rejected() => Assert.NotNull(CheckCreate(CreateWith("ruleset_version", "rules 1")));

    private static SchemaViolation? CheckFile(string schema, string instance)
    {
        var validator = SchemaValidator.Compile(Read(schema));
        using var doc = JsonDocument.Parse(instance);
        return validator.Validate(doc.RootElement);
    }

    [Fact]
    public void IRQ_03_poll_response_valid_example_validates() =>
        Assert.Null(CheckFile("identity-poll-response.schema.json", Read("identity-poll-response.valid.json")));

    [Fact]
    public void IRQ_03_poll_response_with_a_rationale_field_is_rejected() =>
        Assert.NotNull(CheckFile("identity-poll-response.schema.json", Read("identity-poll-response.invalid.json")));

    [Fact]
    public void IRQ_03_poll_response_requires_next_cursor_and_allows_null()
    {
        var node = JsonNode.Parse(Read("identity-poll-response.valid.json"))!.AsObject();
        node["next_cursor"] = null;
        Assert.Null(CheckFile("identity-poll-response.schema.json", node.ToJsonString()));
        node.Remove("next_cursor");
        Assert.NotNull(CheckFile("identity-poll-response.schema.json", node.ToJsonString()));
    }

    [Fact]
    public void IRQ_03_poll_item_with_a_deferred_decision_is_rejected()
    {
        var node = JsonNode.Parse(Read("identity-poll-response.valid.json"))!.AsObject();
        node["items"]![0]!["decision"] = "defer";
        Assert.NotNull(CheckFile("identity-poll-response.schema.json", node.ToJsonString()));
    }

    [Theory]
    [InlineData("superseded_by_rule")]
    [InlineData("source_changed")]
    [InlineData("ruleset_retired")]
    public void IRQ_09_withdraw_accepts_the_three_reasons(string reason) =>
        Assert.Null(CheckFile("identity-withdraw.schema.json", "{\"reason\":\"" + reason + "\"}"));

    [Fact]
    public void IRQ_09_withdraw_valid_and_invalid_examples()
    {
        Assert.Null(CheckFile("identity-withdraw.schema.json", Read("identity-withdraw.valid.json")));
        Assert.NotNull(CheckFile("identity-withdraw.schema.json", Read("identity-withdraw.invalid.json")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reason\":\"source_changed\",\"extra\":1}")]
    public void IRQ_09_withdraw_requires_exactly_reason(string body) =>
        Assert.NotNull(CheckFile("identity-withdraw.schema.json", body));

    [Fact]
    public void IRQ_11_create_link_challenge_subtype_is_rejected_by_the_schema()
    {
        var v = CheckCreate(Read("identity-create.link-challenge.invalid.json"));
        Assert.NotNull(v);
        Assert.Equal("/subtype", v!.Pointer);
    }
}
