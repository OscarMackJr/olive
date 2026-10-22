using System.Text;
using System.Text.Json;
using Olive.Domain;

namespace Olive.Tests;

public sealed class JcsTests
{
    private static string Canon(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Encoding.UTF8.GetString(Jcs.Canonicalize(doc.RootElement));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0x0000000000000000UL, "0")]
    [InlineData(0x8000000000000000UL, "0")]
    [InlineData(0x0000000000000001UL, "5e-324")]
    [InlineData(0x8000000000000001UL, "-5e-324")]
    [InlineData(0x7fefffffffffffffUL, "1.7976931348623157e+308")]
    [InlineData(0xffefffffffffffffUL, "-1.7976931348623157e+308")]
    [InlineData(0x4340000000000000UL, "9007199254740992")]
    [InlineData(0xc340000000000000UL, "-9007199254740992")]
    [InlineData(0x4430000000000000UL, "295147905179352830000")]
    [InlineData(0x44b52d02c7e14af5UL, "9.999999999999997e+22")]
    [InlineData(0x44b52d02c7e14af6UL, "1e+23")]
    [InlineData(0x44b52d02c7e14af7UL, "1.0000000000000001e+23")]
    [InlineData(0x444b1ae4d6e2ef4eUL, "999999999999999700000")]
    [InlineData(0x444b1ae4d6e2ef4fUL, "999999999999999900000")]
    [InlineData(0x444b1ae4d6e2ef50UL, "1e+21")]
    [InlineData(0x3eb0c6f7a0b5ed8cUL, "9.999999999999997e-7")]
    [InlineData(0x3eb0c6f7a0b5ed8dUL, "0.000001")]
    [InlineData(0x41b3de4355555553UL, "333333333.3333332")]
    [InlineData(0x41b3de4355555554UL, "333333333.33333325")]
    [InlineData(0x41b3de4355555555UL, "333333333.3333333")]
    [InlineData(0x41b3de4355555556UL, "333333333.3333334")]
    [InlineData(0x41b3de4355555557UL, "333333333.33333343")]
    [InlineData(0xbecbf647612f3696UL, "-0.0000033333333333333333")]
    [InlineData(0x43143ff3c1cb0959UL, "1424953923781206.2")]
    public void API_09_appendix_b_number_serialization(ulong bits, string expected)
    {
        Assert.Equal(expected, Jcs.FormatNumber(BitConverter.UInt64BitsToDouble(bits)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("4.50", "4.5")]
    [InlineData("2e-3", "0.002")]
    [InlineData("1E30", "1e+30")]
    [InlineData("0.000000000000000000000000001", "1e-27")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    [InlineData("1.0", "1")]
    [InlineData("-0", "0")]
    [InlineData("9007199254740992", "9007199254740992")]
    public void API_09_number_spellings_canonicalize(string token, string expected)
    {
        Assert.Equal(expected, Canon(token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_rfc_section_3_2_2_sample_matches_expected_bytes()
    {
        var input = "{\"numbers\":[333333333.33333329,1E30,4.50,2e-3,0.000000000000000000000000001],\"string\":\"\\u20ac$\\u000F\\u000aA'\\u0042\\u0022\\u005c\\\\\\\"\\/\",\"literals\":[null,true,false]}";
        const string expected = "{\"literals\":[null,true,false],\"numbers\":[333333333.3333333,1e+30,4.5,0.002,1e-27],\"string\":\"\u20ac$\\u000f\\nA'B\\\"\\\\\\\\\\\"/\"}";
        Assert.Equal(expected, Canon(input));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_keys_sorted_by_utf16_code_units_astral_before_bmp_high()
    {
        var o = Canon("{\"\\uff5e\":1,\"\\ud83d\\ude00\":2,\"\\u20ac\":3}");
        Assert.True(o.IndexOf("\u20ac", StringComparison.Ordinal) < o.IndexOf("\ud83d\ude00", StringComparison.Ordinal));
        Assert.True(o.IndexOf("\ud83d\ude00", StringComparison.Ordinal) < o.IndexOf("\uff5e", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_keys_sorted_recursively_and_arrays_keep_order()
    {
        Assert.Equal("{\"a\":[{\"x\":1,\"y\":2},3,2,1],\"b\":{\"c\":1,\"d\":2}}", Canon("{ \"b\": {\"d\":2,\"c\":1}, \"a\": [ {\"y\":2,\"x\":1}, 3, 2, 1 ] }"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_string_escapes_are_minimal_and_non_ascii_literal()
    {
        var o = Canon("\"\\b\\t\\n\\f\\r\\u0001\\u001F/\\u007f\\u00e9\\\"\\\\\"");
        Assert.Equal("\"\\b\\t\\n\\f\\r\\u0001\\u001f/\u007f\u00e9\\\"\\\\\"", o);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_output_is_utf8_without_bom()
    {
        using var doc = JsonDocument.Parse("\"\u20ac\"");
        Assert.Equal(new byte[] { 0x22, 0xe2, 0x82, 0xac, 0x22 }, Jcs.Canonicalize(doc.RootElement));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_literals_verbatim()
    {
        Assert.Equal("[true,false,null]", Canon("[ true , false , null ]"));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("0.1")]
    [InlineData("100")]
    [InlineData("1E2")]
    [InlineData("5e-324")]
    [InlineData("-0")]
    public void API_09_lossless_tokens(string token) => Assert.True(JsonNumbers.IsLossless(token));

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("9007199254740993")]
    [InlineData("333333333.33333329")]
    [InlineData("0.1000000000000000055511151231257827")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void API_09_lossy_tokens(string token) => Assert.False(JsonNumbers.IsLossless(token));

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_proposal_hash_uses_exactly_the_four_keys()
    {
        using var a = JsonDocument.Parse("{\"action_type\":\"x@1\",\"party_ref\":\"p\",\"parameters\":{\"b\":1,\"a\":2},\"justification\":\"j\",\"nono_session_id\":\"s1\"}");
        using var b = JsonDocument.Parse("{\"justification\":\"j\",\"parameters\":{\"a\":2,\"b\":1},\"party_ref\":\"p\",\"action_type\":\"x@1\",\"other\":5}");
        Assert.Equal(Jcs.ProposalHash(a.RootElement), Jcs.ProposalHash(b.RootElement));
        Assert.Equal("{\"action_type\":\"x@1\",\"justification\":\"j\",\"parameters\":{\"a\":2,\"b\":1},\"party_ref\":\"p\"}", Encoding.UTF8.GetString(Jcs.CanonicalizeProposalFields(a.RootElement)));
        Assert.Equal(Jcs.Sha256Hex(Jcs.CanonicalizeProposalFields(a.RootElement)), Jcs.ProposalHash(a.RootElement));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("action_type")]
    [InlineData("party_ref")]
    [InlineData("parameters")]
    [InlineData("justification")]
    public void API_09_proposal_hash_missing_key_throws(string missing)
    {
        var all = new Dictionary<string, object> { ["action_type"] = "x@1", ["party_ref"] = "p", ["parameters"] = new { }, ["justification"] = "j" };
        all.Remove(missing);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(all));
        Assert.Throws<ArgumentException>(() => Jcs.ProposalHash(doc.RootElement));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_09_sha256_hex_is_lowercase()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Jcs.Sha256Hex(Encoding.UTF8.GetBytes("abc")));
    }
}
