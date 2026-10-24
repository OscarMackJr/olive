using Npgsql;

namespace Olive.Tests;

// Reusable multi-source secret search (IDN-08). Each source is named text an observer could read.
// The JSONL execution output is added as a further source through AddSource (AddJsonl). The scan otherwise covers
// HTTP responses (body and every header), captured logs and stored rows (queue_item is excluded: it stores the rationale by design).
internal sealed class SentinelScan
{
    private readonly List<(string Name, string Text)> _sources = [];

    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToArray();

    public void AddSource(string name, string text) => _sources.Add((name, text));

    // Reads the executor output with FileShare.ReadWrite so a live writer does not block the scan.
    public void AddJsonl(string path)
    {
        var text = "";
        if (File.Exists(path))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
        }

        AddSource("jsonl", text);
    }

    public async Task AddResponseAsync(string name, HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var headers = res.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value))
            .Concat(res.Content.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value)))
            .Append("Location: " + res.Headers.Location);
        AddSource(name + " body", body);
        AddSource(name + " headers", string.Join("\n", headers));
    }

    public async Task AddRowsAsync(TestDatabase db, params Guid[] tenants)
    {
        foreach (var table in new[] { "proposal", "proposal_event", "execution_ledger" })
        {
            var text = new List<string>();
            foreach (var tenant in tenants)
            {
                await using var conn = await ApiProposalTests.OpenReaderAsync(db, tenant);
                await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
                await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
                {
                    set.Parameters.AddWithValue("t", tenant.ToString());
                    await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }

                await using var cmd = new NpgsqlCommand($"select row_to_json(t)::text from {table} t", conn, tx);
                await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken)) { text.Add(reader.GetString(0)); }
            }

            AddSource("rows:" + table, string.Join("\n", text));
        }
    }

    // The common set for an integration scenario: responses, logs and stored rows for both seeded tenants.
    public static async Task<SentinelScan> CollectAsync(OliveApiFactory factory, TestDatabase db, params HttpResponseMessage[] responses)
    {
        var scan = new SentinelScan();
        for (var i = 0; i < responses.Length; i++) { await scan.AddResponseAsync("response" + i, responses[i]); }
        scan.AddSource("logs", factory.Logs.Text);
        await scan.AddRowsAsync(db, TestPaths.TenantAlpha, TestPaths.TenantBeta);
        return scan;
    }

    // Names the offending source and a redacted hint of the sentinel; never prints the sentinel value.
    public void AssertAbsent(params string[] sentinels)
    {
        foreach (var sentinel in sentinels)
        {
            var hits = _sources.Where(s => s.Text.Contains(sentinel, StringComparison.Ordinal)).Select(s => s.Name).ToArray();
            if (hits.Length > 0)
            {
                Assert.Fail($"A sentinel secret (length {sentinel.Length}, starts '{sentinel[..Math.Min(4, sentinel.Length)]}...') appeared in: {string.Join(", ", hits)}");
            }
        }
    }
}
