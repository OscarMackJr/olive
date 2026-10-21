using Npgsql;

namespace Olive.Persistence;

public static class PostgresUrl
{
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

            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv[0] == "sslmode")
                {
                    if (kv.Length != 2 || !Enum.TryParse<SslMode>(Uri.UnescapeDataString(kv[1]), true, out var mode))
                    {
                        throw new FormatException();
                    }

                    builder.SslMode = mode;
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
