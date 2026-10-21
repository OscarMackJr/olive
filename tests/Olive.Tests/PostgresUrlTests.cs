using Npgsql;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public class PostgresUrlTests
{
    [Fact]
    public void DB_02_postgres_url_decodes_password_and_parts()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresUrl.ToNpgsql("postgresql://u:p%40ss%25w@h:5433/olive?sslmode=require"));
        Assert.Equal("h", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("olive", b.Database);
        Assert.Equal("u", b.Username);
        Assert.Equal("p@ss%w", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void DB_02_postgres_url_defaults_port()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresUrl.ToNpgsql("postgresql://u:p@h/olive"));
        Assert.Equal(5432, b.Port);
    }

    [Fact]
    public void DB_02_postgres_url_accepts_postgres_scheme()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresUrl.ToNpgsql("postgres://u:p@h:5432/olive"));
        Assert.Equal("h", b.Host);
    }

    [Theory]
    [InlineData("mysql://u:secretpw@h/olive")]
    [InlineData("")]
    [InlineData("postgresql://u:secretpw@h")]
    [InlineData("postgresql://u:secretpw@h/olive?sslmode=bogus")]
    public void DB_02_postgres_url_rejects_invalid_with_static_message(string input)
    {
        var ex = Assert.Throws<FormatException>(() => PostgresUrl.ToNpgsql(input));
        Assert.Equal("Invalid Postgres URL.", ex.Message);
        Assert.DoesNotContain("secretpw", ex.Message);
    }

    [Fact]
    public void DB_02_postgres_url_output_flags()
    {
        var s = PostgresUrl.ToNpgsql("postgresql://u:p@h/olive");
        Assert.DoesNotContain("Include Error Detail", s);
        var b = new NpgsqlConnectionStringBuilder(s);
        Assert.Equal("olive", b.ApplicationName);
        Assert.True(b.Pooling);
    }
}
