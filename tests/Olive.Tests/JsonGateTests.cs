using System.Text;
using Olive.Host.Endpoints;

namespace Olive.Tests;

public sealed class JsonGateTests
{
    private static bool Admit(string body) => Admit(Encoding.UTF8.GetBytes(body));

    private static bool Admit(byte[] bytes)
    {
        var ok = JsonGate.TryAdmit(bytes, out var doc, out _);
        Assert.Equal(ok, doc is not null);
        doc?.Dispose();
        return ok;
    }

    // Escapes are spelled as backslash text, never as a literal surrogate char in C# source.
    private const string HighSur = "\\ud800";
    private const string LowSur = "\\udc00";

    public static TheoryData<string> Rejected() => new()
    {
        "{\"run_id\":\"r\",\"a\":1,\"a\":2}",
        "{\"run_id\":\"r\",\"p\":{\"a\":1,\"a\":2}}",
        "{\"run_id\":\"r\",\"p\":{\"a\":1,\"\\u0061\":2}}",
        "{\"run_id\":\"r\",\"p\":[{\"x\":{\"k\":1,\"k\":1}}]}",
        "[]",
        "1",
        "\"r\"",
        "{\"run_id\":\"r\",\"p\":{\"" + HighSur + "\":1}}",
        "{\"run_id\":\"r\",\"p\":{\"" + LowSur + "\":1}}",
        "{\"run_id\":\"r\",\"p\":\"" + HighSur + "\"}",
        "{\"run_id\":\"r\",\"p\":\"x" + LowSur + "\"}",
        "{\"run_id\":\"r\",\"p\":\"\\u0000\"}",
        "{\"run_id\":\"r\",\"\\u0000\":1}",
        "{\"run_id\":\"r\",\"d\":" + "[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[" + "]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]" + "}",
        "{\"run_id\":\"r\",\"n\":9007199254740993}",
        "{\"run_id\":\"r\",\"p\":{\"n\":333333333.33333329}}",
        "{\"run_id\":\"r\",\"nono_session_id\":9007199254740993}",
        "{\"run_id\":\"r\",\"n\":" + new string('1', 65) + "}",
        "{\"run_id\":\"r\",\"n\":1e309}",
        "{\"run_id\":\"r\",\"n\":1e-325}",
        "{\"run_id\":\"r\",\"n\":1e0000007}",
        "{}",
        "{\"run_id\":1}",
        "{\"run_id\":null}",
        "{\"run_id\":\"\"}",
        "{\"run_id\":\"" + new string('x', 257) + "\"}",
        "{\"run_id\":\"" + HighSur + "\"}",
        "{\"run_id\":\"\\u0000\"}",
        "not json",
        "",
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    [Trait("Category", "Unit")]
    public void API_04_gate_rejects_non_ijson_bodies(string body) => Assert.False(Admit(body));

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_gate_rejects_expansion_budget_overflow()
    {
        // Each 1e308-style token charges roughly 300 over its wire size; enough of them exceed the 65536 budget.
        var tokens = string.Join(",", Enumerable.Repeat("1e300", 400));
        Assert.False(Admit("{\"run_id\":\"r\",\"n\":[" + tokens + "]}"));
        Assert.True(Admit("{\"run_id\":\"r\",\"n\":[" + string.Join(",", Enumerable.Repeat("1e300", 10)) + "]}"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_gate_rejects_oversize_exponent_token_without_throwing()
    {
        Assert.False(Admit("{\"run_id\":\"r\",\"n\":1e99999999999}"));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("1e0")]
    [InlineData("-0")]
    [InlineData("0.1")]
    [InlineData("5e-324")]
    [Trait("Category", "Unit")]
    public void API_04_gate_admits_exact_number_spellings_in_any_position(string n)
    {
        Assert.True(Admit("{\"run_id\":\"r\",\"top\":" + n + ",\"parameters\":{\"p\":" + n + ",\"l\":[" + n + "]}}"));
        Assert.True(Admit("{\"run_id\":\"r\",\"nono_session_id\":" + n + "}"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_gate_returns_run_id_unmodified()
    {
        Assert.True(JsonGate.TryAdmit(Encoding.UTF8.GetBytes("{\"run_id\":\"  Run 1 \"}"), out var doc, out var runId));
        using (doc)
        {
            Assert.Equal("  Run 1 ", runId);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, doc!.RootElement.ValueKind);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_gate_admits_max_length_run_id_and_depth_64()
    {
        Assert.True(Admit("{\"run_id\":\"" + new string('x', 256) + "\"}"));
        var nested = "{\"run_id\":\"r\",\"d\":" + string.Concat(Enumerable.Repeat("[", 62)) + string.Concat(Enumerable.Repeat("]", 62)) + "}";
        Assert.True(Admit(nested));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_gate_rejects_raw_invalid_utf8_without_throwing()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"run_id\":\"r\",\"x\":\"").Concat(new byte[] { 0xC3, 0x28 }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        Assert.False(Admit(bytes));
    }
}
