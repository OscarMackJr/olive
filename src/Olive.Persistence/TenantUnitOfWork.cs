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
}
