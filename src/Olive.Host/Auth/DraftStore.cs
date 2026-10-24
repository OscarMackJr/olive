using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Olive.Host.Auth;

// The decision and rationale a reviewer typed before an Entra re-authentication. Never serialised or logged.
public sealed record DraftSnapshot(string Decision, string Rationale)
{
    public override string ToString() => "[REDACTED]";
}

// D-05: server-side, single-use, oid- and item-bound, short-lived draft keyed by a random 256-bit opaque key. Only the key travels in a URL.
public sealed class DraftStore(TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    public const int MaxEntries = 1000;

    private sealed record Entry(string Oid, Guid ItemId, DraftSnapshot Snapshot, DateTimeOffset Expires, long Sequence);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _sequence;

    public string Put(string oid, Guid itemId, string decision, string rationale)
    {
        var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        lock (_gate)
        {
            foreach (var (k, e) in _entries.ToArray())
            {
                if (e.Expires <= now) { _entries.TryRemove(k, out _); }
            }

            while (_entries.Count >= MaxEntries)
            {
                var oldest = _entries.MinBy(p => p.Value.Sequence).Key;
                _entries.TryRemove(oldest, out _);
            }

            _entries[key] = new Entry(oid, itemId, new DraftSnapshot(decision, rationale), now + Ttl, ++_sequence);
        }

        return key;
    }

    // Removes the entry on the first successful read. A mismatch (wrong oid or item) leaves it in place so guessing cannot burn a draft.
    public DraftSnapshot? Take(string key, string oid, Guid itemId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) { return null; }
            if (entry.Expires <= now)
            {
                _entries.TryRemove(key, out _);
                return null;
            }

            if (!string.Equals(entry.Oid, oid, StringComparison.Ordinal) || entry.ItemId != itemId) { return null; }
            _entries.TryRemove(key, out _);
            return entry.Snapshot;
        }
    }
}
