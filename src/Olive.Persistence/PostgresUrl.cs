using Npgsql;

namespace Olive.Persistence;

public static class PostgresUrl
{
    private static readonly Dictionary<string, SslMode> SslModes = new(StringComparer.Ordinal)
    {
        ["disable"] = SslMode.Disable,
        ["allow"] = SslMode.Allow,
        ["prefer"] = SslMode.Prefer,
        ["require"] = SslMode.Require,
        ["verify-ca"] = SslMode.VerifyCA,
        ["verify-full"] = SslMode.VerifyFull,
    };

    private static string RequireValue(string value) => string.IsNullOrWhiteSpace(value) ? throw new FormatException() : value;

    public static string ToNpgsql(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                throw new FormatException();
            }

            if (uri.Scheme != "postgresql" && uri.Scheme != "postgres")
            {
                throw new FormatException();
            }

            var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrEmpty(uri.Host) || string.IsNullOrEmpty(database))
            {
                throw new FormatException();
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Database = database,
                ApplicationName = "olive",
                Pooling = true,
            };

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                builder.Username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length == 2)
                {
                    builder.Password = Uri.UnescapeDataString(parts[1]);
                }
            }

            // libpq spellings only; an unknown or repeated parameter fails closed rather than being ignored (WR-06).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2 || !seen.Add(kv[0]))
                {
                    throw new FormatException();
                }

                var value = Uri.UnescapeDataString(kv[1]);
                switch (kv[0])
                {
                    case "sslmode":
                        builder.SslMode = SslModes.TryGetValue(value, out var mode) ? mode : throw new FormatException();
                        break;
                    case "sslrootcert":
                        builder.RootCertificate = RequireValue(value);
                        break;
                    case "sslcert":
                        builder.SslCertificate = RequireValue(value);
                        break;
                    case "sslkey":
                        builder.SslKey = RequireValue(value);
                        break;
                    default:
                        throw new FormatException();
                }
            }

            return builder.ConnectionString;
        }
        catch (Exception)
        {
            throw new FormatException("Invalid Postgres URL.");
        }
    }
}
