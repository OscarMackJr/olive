using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
using Olive.Catalog;
using Olive.Host.Auth;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ServiceTokenAuthTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private const string AlphaToken = "fixture-service-token-bluto-alpha";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ReasonOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString() ?? "";

    [Fact]
    public async Task IRQ_04_service_token_is_verified_through_the_service_verifier_not_the_agent_introspector()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.GetAsync(Items, Ct);

        // Authenticated, then refused as a wrong caller type for the reviewer-only list route (interim until the poll view).
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(1, factory.ServiceTokens.Calls);
        Assert.Equal(0, factory.Introspector.Calls);
    }

    [Fact]
    public async Task IRQ_04_dev_reviewer_header_wins_over_authorization_and_still_works()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateReviewerClient("alpha-sup-1");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AlphaToken);
        var response = await client.GetAsync(Items, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, factory.ServiceTokens.Calls);
    }

    [Fact]
    public async Task IRQ_04_no_credentials_is_reviewer_unauthenticated_unchanged()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateClient();
        var response = await client.GetAsync(Items, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("reviewer_unauthenticated", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
    }

    [Theory]
    [InlineData("fixture-service-token-inactive")]
    [InlineData("fixture-service-token-expired")]
    [InlineData("not-a-known-token")]
    public async Task IRQ_04_inactive_expired_or_unknown_tokens_get_401_with_bearer_challenge(string token)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateServiceClient(token);
        var response = await client.GetAsync(Items, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task IRQ_04_verifier_outage_fails_closed_with_503()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton<IServiceTokenVerifier>(new FaultingServiceTokenVerifier()));
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.GetAsync(Items, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("introspection_unavailable", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task IRQ_04_malformed_verifier_answer_fails_closed_with_503()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateServiceClient("fixture-service-token-malformed");
        var response = await client.GetAsync(Items, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("fixture-key-liam-alpha")]
    [InlineData("fixture-key-user")]
    [InlineData("fixture-service-token-delegated-agent")]
    public async Task IRQ_04_agent_user_and_delegated_callers_are_refused_on_reviewer_routes(string token)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateServiceClient(token);
        var response = await client.GetAsync(Items, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", ReasonOf(body));
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain(TestPaths.TenantAlpha.ToString(), body);
        Assert.DoesNotContain("bluto-stub", body);
        Assert.Equal(0, factory.Introspector.Calls);
    }

    [Fact]
    public async Task IDN_03_service_token_is_refused_on_proposals_and_catalog()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateServiceClient(AlphaToken);
        // IDN-03 is satisfied by 401 or 403. Popeye's session-key introspection answers inactive for a service token (fixture
        // index maps it to inactive.json), which gives 401 key_inactive here; 403 would equally be a refusal.
        foreach (var path in new[] { "/v1/catalog/1", "/v1/proposals/00000000-0000-4000-8000-0000000000aa" })
        {
            var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("key_inactive", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
        }

        Assert.Equal(0, factory.ServiceTokens.Calls);
    }

    [Fact]
    public async Task IRQ_04_token_text_never_appears_in_trace_level_logs()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        foreach (var token in new[] { AlphaToken, "fixture-service-token-inactive", "fixture-service-token-malformed" })
        {
            using var client = factory.CreateServiceClient(token);
            await client.GetAsync(Items, Ct);
        }

        Assert.DoesNotContain("fixture-service-token", factory.Logs.Text);
    }

    private static DefaultHttpContext Context(string token, IServiceCollection? _ = null)
    {
        var verifier = Olive.Integrations.Popeye.StubServiceTokenVerifier.CreateDefault(TimeProvider.System);
        var result = verifier.VerifyAsync(new Olive.Domain.InboundBearerToken(token), CancellationToken.None).GetAwaiter().GetResult();
        var http = new DefaultHttpContext();
        if (result.Caller is { } caller)
        {
            http.User = ServiceClaims.ToPrincipal(caller, ServiceTokenAuthenticationHandler.SchemeName);
        }

        return http;
    }

    private static readonly IProducerRegistry Registry = ProducerRegistry.LoadEmbedded();
    private static readonly IReviewerTenantMap Tenants = ReviewerTenantMap.LoadEmbedded();

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_04_producer_check_resolves_producer_tenant_and_client_from_the_token()
    {
        var refusal = QueueCallerFilters.ProducerCheck(Context(AlphaToken), Registry, Tenants, out var context);
        Assert.Null(refusal);
        Assert.Equal(TestPaths.TenantAlpha, context!.TenantId);
        Assert.Equal("bluto", context.Producer);
        Assert.Equal("bluto-stub", context.ClientId);
    }

    [Theory]
    [InlineData("fixture-service-token-delegated-agent", "principal_not_permitted")]
    [InlineData("fixture-key-liam-alpha", "principal_not_permitted")]
    [InlineData("fixture-service-token-unknown-client", "producer_not_registered")]
    [InlineData("fixture-service-token-unknown-tenant", "tenant_not_permitted")]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_producer_check_refuses_with_static_codes(string token, string reason)
    {
        var refusal = QueueCallerFilters.ProducerCheck(Context(token), Registry, Tenants, out var context);
        Assert.NotNull(refusal);
        Assert.Null(context);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await refusal.ExecuteAsync(http);
        Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync(Ct);
        Assert.Equal(reason, ReasonOf(body));
        Assert.DoesNotContain("bluto-stub", body);
        Assert.DoesNotContain("unregistered-client", body);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void T_08_11_service_principal_is_never_accepted_by_reviewer_check_despite_the_shared_tenant_claim()
    {
        var http = Context(AlphaToken);
        Assert.NotNull(http.User.FindFirst(ReviewerClaims.TenantId));
        Assert.NotNull(http.User.ToReviewerContext());
        Assert.NotNull(QueueCallerFilters.ReviewerCheck(http));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void T_08_10_reviewer_principal_is_not_a_producer()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ReviewerClaims.TenantId, TestPaths.TenantAlpha.ToString())], DevReviewerAuthenticationHandler.SchemeName)),
        };
        Assert.Null(QueueCallerFilters.ReviewerCheck(http));
        Assert.NotNull(QueueCallerFilters.ProducerCheck(http, Registry, Tenants, out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_04_queue_caller_selector_uses_header_shape_only()
    {
        var both = new DefaultHttpContext();
        both.Request.Headers.Authorization = "Bearer x";
        both.Request.Headers[DevReviewerAuthenticationHandler.HeaderName] = "alpha-sup-1";
        Assert.Equal("DevReviewer", QueueCaller.Select(both));
        var bearer = new DefaultHttpContext();
        bearer.Request.Headers.Authorization = "Bearer x";
        Assert.Equal("ServiceToken", QueueCaller.Select(bearer));
        Assert.Equal("DevReviewer", QueueCaller.Select(new DefaultHttpContext()));
    }
}
