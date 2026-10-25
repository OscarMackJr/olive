using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Olive.Application;
using Olive.Host.Auth;
using Olive.Integrations.Entra;
using Olive.Domain;

namespace Olive.Tests;

public sealed class ReviewerSessionTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/review")]
    [InlineData("/dev/sign-in")]
    [InlineData("/v1/queue/items")]
    [Trait("Category", "Integration")]
    public async Task UI_01_default_agent_scheme_ignores_non_agent_routes_and_never_introspects(string path)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var before = factory.Introspector.Calls;
        await client.GetAsync(path, Ct);
        Assert.Equal(before, factory.Introspector.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_cookie_principal_round_trips_to_the_same_reviewer_context_as_the_dev_header_scheme()
    {
        var source = FixtureReviewerIdentitySource.CreateDefault();
        var identity = source.Find("alpha-sup-1")!;
        var tenant = Guid.NewGuid();
        var header = ReviewerClaims.ToPrincipal(identity, tenant, QueueStates.Stub, DevReviewerAuthenticationHandler.SchemeName);
        var cookie = ReviewerClaims.WithSession(
            ReviewerClaims.ToPrincipal(identity, tenant, QueueStates.Stub, ReviewerCookie.Scheme), DateTimeOffset.UnixEpoch, identity.ReviewerId, null, null);
        Assert.Equal(header.ToReviewerContext()!.TenantId, cookie.ToReviewerContext()!.TenantId);
        Assert.Equal(header.ToReviewerContext()!.Oid, cookie.ToReviewerContext()!.Oid);
        Assert.Equal(header.ToReviewerContext()!.AuthSource, cookie.ToReviewerContext()!.AuthSource);
        Assert.Equal(header.ToReviewerContext()!.Groups, cookie.ToReviewerContext()!.Groups);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_picker_source_lists_all_fixture_reviewers_in_ordinal_order()
    {
        var all = FixtureReviewerIdentitySource.CreateDefault().All();
        Assert.Equal(11, all.Count);
        foreach (var id in new[] { "alpha-steward-1", "alpha-steward-2", "beta-steward" })
            Assert.Contains(id, all.Select(a => a.ReviewerId));
        Assert.Equal(all.Select(a => a.ReviewerId).OrderBy(x => x, StringComparer.Ordinal), all.Select(a => a.ReviewerId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UI_01_absolute_cap_rejects_a_session_older_than_8_hours_even_when_sliding_kept_it_alive()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await using var app = await StartAsync(clock);
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/signin", Ct)).StatusCode);
        for (var i = 0; i < 23; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/whoami", Ct)).StatusCode);
        }

        // 7 h 40 min after sign-in: still inside the cap, kept alive by sliding renewal.
        clock.Advance(TimeSpan.FromMinutes(20));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/whoami", Ct)).StatusCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UI_01_idle_session_over_30_minutes_is_rejected()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await using var app = await StartAsync(clock);
        using var client = Client(app);
        await client.GetAsync("/signin", Ct);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/whoami", Ct)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/whoami", Ct)).StatusCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UI_01_session_cookie_has_host_prefix_and_hardening_attributes()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        await using var app = await StartAsync(clock);
        using var client = Client(app);
        var response = await client.GetAsync("/signin", Ct);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-olive-reviewer=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    // The TestServer client has no cookie jar; this replays only the reviewer cookie, like a browser would.
    private static HttpClient Client(WebApplication app) =>
        new(new CookieJar(app.GetTestServer().CreateHandler())) { BaseAddress = new Uri("https://localhost") };

    private sealed class CookieJar(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private string? _cookie;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_cookie is not null) { request.Headers.Add("Cookie", _cookie); }
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var v in values.Where(v => v.StartsWith("__Host-olive-reviewer=", StringComparison.Ordinal)))
                {
                    var pair = v.Split(';')[0];
                    _cookie = pair.EndsWith('=') ? null : pair;
                }
            }

            return response;
        }
    }

    // A minimal host holding only the cookie scheme, so the cap is proven without a database.
    private static async Task<WebApplication> StartAsync(TimeProvider clock)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(clock);
        builder.Services.AddAuthentication().AddReviewerSession();
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/signin", async (HttpContext http, TimeProvider time) =>
        {
            var identity = FixtureReviewerIdentitySource.CreateDefault().Find("alpha-sup-1")!;
            var principal = ReviewerClaims.WithSession(
                ReviewerClaims.ToPrincipal(identity, Guid.NewGuid(), QueueStates.Stub, ReviewerCookie.Scheme), time.GetUtcNow(), identity.ReviewerId, null, null);
            await ReviewerCookie.SignInAsync(http, principal);
            return Results.Ok();
        });
        app.MapGet("/whoami", async (HttpContext http) =>
        {
            var result = await http.AuthenticateAsync(ReviewerCookie.Scheme);
            return result.Succeeded ? Results.Ok() : Results.Unauthorized();
        });
        await app.StartAsync();
        return app;
    }
}
