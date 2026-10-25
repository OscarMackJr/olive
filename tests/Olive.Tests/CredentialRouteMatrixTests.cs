using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Olive.Host.Auth;
using Olive.Host.Startup;

namespace Olive.Tests;

public enum Cred { None, AgentKey, DevHeader, DevCookie, OidcCookie, ForgedCookie, ExpiredCookie, ServiceToken }

public enum Route { Proposals, Catalog, QueueItems, Review, DevSignIn }

// Expected admission for one cell. Status is pinned explicitly; Reason applies to JSON routes, LocationPrefix to redirects.
public sealed record Expected(HttpStatusCode Status, string? Reason = null, string? LocationPrefix = null);

// Shared by every cell of the matrix: one host in ci with the mock IdP and one credential of each kind, obtained once.
public sealed class MatrixState : IAsyncDisposable
{
    private Task<MatrixHost>? _host;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public Task<MatrixHost> GetAsync(PostgresFixture pg, MockOidcFixture oidc)
    {
        _host ??= MatrixHost.CreateAsync(pg, oidc);
        return _host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is { IsCompletedSuccessfully: true }) { await _host.Result.DisposeAsync(); }
    }
}

public sealed class MatrixHost : IAsyncDisposable
{
    public required TestDatabase Db { get; init; }
    public required OliveApiFactory Factory { get; init; }
    public required HttpClient Client { get; init; }
    public required string DevCookie { get; init; }
    public required string OidcCookie { get; init; }

    public static async Task<MatrixHost> CreateAsync(PostgresFixture pg, MockOidcFixture oidc)
    {
        var db = await pg.CreateDatabaseAsync();
        var factory = new OliveApiFactory(db, mockOidc: oidc);
        // WR-01: with Entra configured the picker is 404, so the dev cookie cell is minted the way the picker would mint it
        // (stub auth source, server-side identity and tenant) and sealed with this host's cookie protector. It models a stub
        // cookie that reaches an Entra host; anonymous callers cannot obtain one here.
        var devCookie = MintDevCookie(factory, "alpha-sup-1");
        Assert.False(string.IsNullOrEmpty(devCookie));

        // The OIDC cookie comes from the real OpenIdConnect handler against the mock IdP.
        using var real = new OidcFlowDriver(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await real.SignInAsync("role-present", "/review")).StatusCode);
        var oidcCookie = real.GetCookie(ReviewerCookie.CookieName);
        Assert.False(string.IsNullOrEmpty(oidcCookie));
        return new MatrixHost { Db = db, Factory = factory, Client = factory.CreateUiClient(handleCookies: false), DevCookie = devCookie!, OidcCookie = oidcCookie! };
    }

    private static string MintDevCookie(OliveApiFactory factory, string reviewerId)
    {
        var services = factory.Services;
        var identity = services.GetRequiredService<Olive.Application.IReviewerIdentitySource>().Find(reviewerId)!;
        var tenants = services.GetRequiredService<Olive.Application.IReviewerTenantMap>().TenantsFor(identity.Groups).Distinct().ToArray();
        Guid? tenant = tenants.Length == 1 ? tenants[0] : null;
        var now = factory.Clock!.GetUtcNow();
        var principal = ReviewerClaims.WithSession(
            ReviewerClaims.ToPrincipal(identity, tenant, Olive.Domain.QueueStates.Stub, ReviewerCookie.Scheme), now, identity.ReviewerId, null, null);
        var properties = new AuthenticationProperties { IsPersistent = false, IssuedUtc = now, ExpiresUtc = now + ReviewerCookie.IdleTimeout };
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ReviewerCookie.Scheme);
        return options.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, ReviewerCookie.Scheme));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }
}

// UI-07 / D-16: credential type x route group. Only the intended pairings are admitted.
[Trait("Category", "Integration")]
public sealed class CredentialRouteMatrixTests(PostgresFixture pg, MockOidcFixture oidc, MatrixState state) : IClassFixture<MockOidcFixture>, IClassFixture<MatrixState>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Paths = ["/v1/proposals/00000000-0000-4000-8000-0000000000aa", "/v1/catalog/1", "/v1/queue/items", "/review", "/dev/sign-in"];

    public static TheoryData<Cred, Route> Cells()
    {
        var data = new TheoryData<Cred, Route>();
        foreach (var cred in Enum.GetValues<Cred>()) { foreach (var route in Enum.GetValues<Route>()) { data.Add(cred, route); } }
        return data;
    }

    private static readonly Expected AgentUnauthenticated = new(HttpStatusCode.Unauthorized, "key_inactive");
    private static readonly Expected ReviewerUnauthenticated = new(HttpStatusCode.Unauthorized, "reviewer_unauthenticated");
    private static readonly Expected InvalidRequest = new(HttpStatusCode.BadRequest, "invalid_request");
    private static readonly Expected NotPermitted = new(HttpStatusCode.Forbidden, "principal_not_permitted");
    private static readonly Expected ToSignIn = new(HttpStatusCode.Redirect, LocationPrefix: "/auth/sign-in");
    private static readonly Expected Ok = new(HttpStatusCode.OK);

    // The intended admission, written down independently of what the handlers do today.
    public static Expected Expect(Cred cred, Route route) => route switch
    {
        Route.Proposals => cred switch
        {
            Cred.AgentKey => new(HttpStatusCode.NotFound),
            // IDN-03: a service token is refused on the agent routes (ServiceToken falls to AgentUnauthenticated below). IDN-03 is
            // satisfied by 401 or 403. The session-key introspection fixture answers inactive for service tokens (a service token
            // is not a session key), so the cell is 401 key_inactive. The non-agent 403 check lives only in POST /v1/proposals,
            // so it cannot pin 403 for these GET routes.
            Cred.DevHeader => NotPermitted,
            _ => AgentUnauthenticated,
        },
        Route.Catalog => cred switch
        {
            Cred.AgentKey => Ok,
            Cred.DevHeader => NotPermitted,
            _ => AgentUnauthenticated,
        },
        Route.QueueItems => cred switch
        {
            Cred.DevHeader => Ok,
            // D-02: the bare GET (no query) from a service token is not a valid poll, so it is a 400, not a wrong-caller refusal.
            Cred.ServiceToken => InvalidRequest,
            Cred.AgentKey => NotPermitted,
            _ => ReviewerUnauthenticated,
        },
        Route.Review => cred is Cred.DevCookie or Cred.OidcCookie ? Ok : ToSignIn,
        // WR-01: this host has Entra configured, so the dev picker is not a sign-in path for any credential.
        _ => new(HttpStatusCode.NotFound),
    };

    private static string ReasonOf(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString() ?? ""; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }

    private static HttpRequestMessage Build(MatrixHost host, Cred cred, Route route)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Paths[(int)route]);
        switch (cred)
        {
            case Cred.AgentKey: request.Headers.Authorization = new AuthenticationHeaderValue(Olive.Application.AgentKeyHeader.Scheme, ApiProposalTests.AlphaKey); break;
            case Cred.ServiceToken: request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-service-token-bluto-alpha"); break;
            case Cred.DevHeader: request.Headers.Add(DevReviewerAuthenticationHandler.HeaderName, "alpha-sup-1"); break;
            case Cred.DevCookie or Cred.ExpiredCookie: request.Headers.TryAddWithoutValidation("Cookie", ReviewerCookie.CookieName + "=" + host.DevCookie); break;
            case Cred.OidcCookie: request.Headers.TryAddWithoutValidation("Cookie", ReviewerCookie.CookieName + "=" + host.OidcCookie); break;
            case Cred.ForgedCookie:
                var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
                request.Headers.TryAddWithoutValidation("Cookie", ReviewerCookie.CookieName + "=" + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('='));
                break;
        }

        return request;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task UI_07_only_the_intended_credential_route_pairings_are_admitted(Cred cred, Route route)
    {
        var host = await state.GetAsync(pg, oidc);
        var expected = Expect(cred, route);
        await state.Gate.WaitAsync(Ct);
        try
        {
            var clock = host.Factory.Clock!;
            var before = clock.GetUtcNow();
            if (cred == Cred.ExpiredCookie) { clock.Advance(TimeSpan.FromHours(9)); }
            var calls = host.Factory.Introspector.Calls;
            HttpResponseMessage response;
            try { response = await host.Client.SendAsync(Build(host, cred, route), Ct); }
            finally { clock.Set(before); }

            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.Equal(expected.Status, response.StatusCode);
            if (expected.Reason is not null) { Assert.Equal(expected.Reason, ReasonOf(body)); }
            if (expected.LocationPrefix is not null)
            {
                var location = response.Headers.Location is { } target ? (target.IsAbsoluteUri ? target.AbsolutePath : target.OriginalString) : "";
                Assert.True(location.StartsWith("/dev/sign-in", StringComparison.Ordinal) || location.StartsWith(expected.LocationPrefix, StringComparison.Ordinal), "unexpected redirect target " + location);
            }

            // The agent key is introspected only on the agent route groups; never on /review, /dev or /v1/queue.
            var agentRoute = route is Route.Proposals or Route.Catalog;
            if (!(agentRoute && cred is Cred.AgentKey or Cred.ServiceToken))
            {
                Assert.Equal(calls, host.Factory.Introspector.Calls);
            }
        }
        finally { state.Gate.Release(); }
    }

    [Fact]
    public void UI_07_cell_table_covers_eight_credentials_by_five_routes()
    {
        Assert.Equal(40, Enum.GetValues<Cred>().Length * Enum.GetValues<Route>().Length);
    }

    [Fact]
    public void UI_07_production_refuses_dev_sign_in_gate_and_http_authority()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DevSignInGate>();
        using var provider = services.BuildServiceProvider();
        var production = new OliveEnvironment("production");
        Assert.Equal("FND-05", Assert.Throws<StartupGuardException>(() => StartupGuards.CheckNoNonProductionComponents(services, provider, production)).Rule);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OLIVE_OIDC_AUTHORITY"] = "http://idp.example/tenant",
            ["OLIVE_OIDC_CLIENT_ID"] = "olive-client",
            ["OLIVE_OIDC_CLIENT_SECRET"] = "secret-value",
        }).Build();
        Assert.Equal("UI-01", Assert.Throws<StartupGuardException>(() => StartupGuards.CheckOidcConfiguration(config, production)).Rule);
    }
}
