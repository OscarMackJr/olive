using Dapper;
using Npgsql;

namespace Olive.Persistence;

// The only tenant-scoped connection entry point (DB-05). The tenant GUC is transaction-local.
public sealed class TenantUnitOfWork(NpgsqlDataSource dataSource)
{
    private const string SetTenant = "select set_config('olive.tenant_id', @t, true)";

    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant is required.", nameof(tenantId));
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteScalarAsync<string>(new CommandDefinition(SetTenant, new { t = tenantId.ToString("D") }, tx, cancellationToken: ct));
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    // EXE-02 / D-02: a lock-holder transaction on its own connection. It never commits: disposing rolls back and releases the lock,
    // and a crashed process drops the connection which releases it too. Null means the lock wait timed out (SQLSTATE 55P03).
    public async Task<IAsyncDisposable?> AcquireAdvisoryLockAsync(Guid tenantId, string lockSql, object parameters, TimeSpan lockTimeout, CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant is required.", nameof(tenantId));
        }

        var conn = await dataSource.OpenConnectionAsync(ct);
        NpgsqlTransaction? tx = null;
        try
        {
            tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteScalarAsync<string>(new CommandDefinition(SetTenant, new { t = tenantId.ToString("D") }, tx, cancellationToken: ct));
            var ms = Math.Max(1, (long)lockTimeout.TotalMilliseconds);
            await conn.ExecuteAsync(new CommandDefinition("set local lock_timeout = " + ms.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(lockSql, parameters, tx, cancellationToken: ct));
            return new LockHandle(conn, tx);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            if (tx is not null) { await tx.DisposeAsync(); }
            await conn.DisposeAsync();
            return null;
        }
        catch
        {
            if (tx is not null) { await tx.DisposeAsync(); }
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class LockHandle(NpgsqlConnection conn, NpgsqlTransaction tx) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await tx.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
