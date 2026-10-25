using System.Buffers.Text;
using System.Text;
using Olive.Application;

namespace Olive.Tests;

// IRQ-03 / D-13: the opaque poll cursor.
[Trait("Category", "Unit")]
public sealed class ProducerCursorTests
{
    private static string Raw(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void IRQ_03_round_trips_microsecond_precision_exactly()
    {
        var at = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234560); // 123456 microseconds
        var id = Guid.NewGuid();

        var cursor = ProducerCursor.Encode(at, id);

        Assert.True(ProducerCursor.TryDecode(cursor, out var decoded, out var decodedId));
        Assert.Equal(at, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.Kind);
        Assert.Equal(id, decodedId);
    }

    [Fact]
    public void IRQ_03_ten_tick_granularity_values_survive()
    {
        var at = new DateTime(2031, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddTicks(10);
        Assert.True(ProducerCursor.TryDecode(ProducerCursor.Encode(at, Guid.Empty), out var decoded, out _));
        Assert.Equal(at.Ticks, decoded.Ticks);
    }

    [Fact]
    public void IRQ_03_cursors_for_equal_decided_at_order_by_item_id()
    {
        var at = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        Assert.True(ProducerCursor.TryDecode(ProducerCursor.Encode(at, low), out var a, out var ai));
        Assert.True(ProducerCursor.TryDecode(ProducerCursor.Encode(at, high), out var b, out var bi));
        Assert.Equal(a, b);
        Assert.True(string.CompareOrdinal(ai.ToString("D"), bi.ToString("D")) < 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("abc+def/ghi=")]
    public void IRQ_03_rejects_empty_and_non_base64url(string? cursor) => Assert.False(ProducerCursor.TryDecode(cursor, out _, out _));

    [Fact]
    public void IRQ_03_rejects_over_128_chars() =>
        Assert.False(ProducerCursor.TryDecode(new string('A', 129), out _, out _));

    [Fact]
    public void IRQ_03_rejects_non_canonical_base64url()
    {
        var good = ProducerCursor.Encode(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), Guid.NewGuid());
        // Flip the unused trailing bits of the last character: same bytes, different string.
        var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var last = alphabet.IndexOf(good[^1]);
        var altered = good[..^1] + alphabet[last ^ 1];
        Assert.False(ProducerCursor.TryDecode(altered, out _, out _));
        Assert.True(ProducerCursor.TryDecode(good, out _, out _));
    }

    [Theory]
    [InlineData("v2.1000000.aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("1000000.aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("v1.12x45.aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("v1.-5.aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("v1..aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("v1.253402300800000000.aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("v1.1000000.not-a-guid")]
    [InlineData("v1.1000000.AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE")]
    [InlineData("v1.1000000.aaaaaaaabbbbccccddddeeeeeeeeeeee.extra")]
    [InlineData("v1.1000000")]
    public void IRQ_03_rejects_malformed_payloads(string text) => Assert.False(ProducerCursor.TryDecode(Raw(text), out _, out _));

    [Fact]
    public void IRQ_03_accepts_a_hand_built_well_formed_cursor() =>
        Assert.True(ProducerCursor.TryDecode(Raw("v1.1000000.aaaaaaaabbbbccccddddeeeeeeeeeeee"), out var at, out var id)
            && at == DateTime.UnixEpoch.AddSeconds(1) && id == Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
}
