using Olive.Host.Auth;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class DraftStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Item = Guid.NewGuid();

    [Fact]
    public void APR_03_draft_is_single_use()
    {
        var store = new DraftStore(new ManualTimeProvider(Start));
        var key = store.Put("oid-1", Item, "approve", "because");
        var first = store.Take(key, "oid-1", Item);
        Assert.Equal("approve", first!.Decision);
        Assert.Equal("because", first.Rationale);
        Assert.Null(store.Take(key, "oid-1", Item));
    }

    [Fact]
    public void APR_03_draft_expires_at_ten_minutes()
    {
        var time = new ManualTimeProvider(Start);
        var store = new DraftStore(time);
        var key = store.Put("oid-1", Item, "approve", "because");
        time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        var alive = store.Put("oid-1", Item, "reject", "x");
        Assert.NotNull(store.Take(key, "oid-1", Item));
        var expired = store.Put("oid-1", Item, "reject", "y");
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(store.Take(expired, "oid-1", Item));
        Assert.Null(store.Take(alive, "oid-1", Item));
    }

    [Fact]
    public void APR_03_wrong_oid_or_item_gets_nothing_and_does_not_consume_the_draft()
    {
        var store = new DraftStore(new ManualTimeProvider(Start));
        var key = store.Put("oid-1", Item, "approve", "because");
        Assert.Null(store.Take(key, "oid-2", Item));
        Assert.Null(store.Take(key, "oid-1", Guid.NewGuid()));
        Assert.NotNull(store.Take(key, "oid-1", Item));
    }

    [Fact]
    public void APR_03_unknown_key_gets_nothing()
    {
        Assert.Null(new DraftStore(new ManualTimeProvider(Start)).Take("nope", "oid-1", Item));
    }

    [Fact]
    public void APR_03_entry_cap_evicts_the_oldest()
    {
        var store = new DraftStore(new ManualTimeProvider(Start));
        var first = store.Put("oid-1", Item, "approve", "first");
        for (var i = 0; i < DraftStore.MaxEntries; i++) { store.Put("oid-1", Item, "approve", "n" + i); }
        Assert.Null(store.Take(first, "oid-1", Item));
    }

    [Fact]
    public void APR_03_snapshot_to_string_is_redacted()
    {
        var snapshot = new DraftSnapshot("approve", "secret-rationale");
        Assert.Equal("[REDACTED]", snapshot.ToString());
        Assert.DoesNotContain("secret-rationale", $"{snapshot}");
    }

    [Fact]
    public void APR_03_key_is_43_chars_base64url_and_unique()
    {
        var store = new DraftStore(new ManualTimeProvider(Start));
        var a = store.Put("oid-1", Item, "approve", "a");
        var b = store.Put("oid-1", Item, "approve", "a");
        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        Assert.NotEqual(a, b);
    }
}
