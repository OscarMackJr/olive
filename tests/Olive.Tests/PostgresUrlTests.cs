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
    [InlineData("postgresql://u:secretpw@h/olive?sslmode=7")]
    [InlineData("postgresql://u:secretpw@h/olive?sslmode=VerifyFull")]
    [InlineData("postgresql://u:secretpw@h/olive?sslmode=Require")]
    [InlineData("postgresql://u:secretpw@h/olive?sslmode")]
    [InlineData("postgresql://u:secretpw@h/olive?sslmode=require&sslmode=disable")]
    [InlineData("postgresql://u:secretpw@h/olive?target_session_attrs=any")]
    [InlineData("postgresql://u:secretpw@h/olive?options=-c%20x%3Dy")]
    [InlineData("postgresql://u:secretpw@h/olive?sslrootcert=")]
    public void DB_02_postgres_url_rejects_invalid_with_static_message(string input)
    {
        var ex = Assert.Throws<FormatException>(() => PostgresUrl.ToNpgsql(input));
        Assert.Equal("Invalid Postgres URL.", ex.Message);
        Assert.DoesNotContain("secretpw", ex.Message);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("allow", SslMode.Allow)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    public void DB_02_postgres_url_accepts_libpq_sslmode_spellings(string value, SslMode expected)
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresUrl.ToNpgsql("postgresql://u:p@h/olive?sslmode=" + value));
        Assert.Equal(expected, b.SslMode);
    }

    [Fact]
    public void DB_02_postgres_url_maps_tls_file_parameters()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresUrl.ToNpgsql(
            "postgresql://u:p@h/olive?sslmode=verify-full&sslrootcert=%2Fetc%2Fca.pem&sslcert=%2Fetc%2Fc.pem&sslkey=%2Fetc%2Fk.pem"));
        Assert.Equal(SslMode.VerifyFull, b.SslMode);
        Assert.Equal("/etc/ca.pem", b.RootCertificate);
        Assert.Equal("/etc/c.pem", b.SslCertificate);
        Assert.Equal("/etc/k.pem", b.SslKey);
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
