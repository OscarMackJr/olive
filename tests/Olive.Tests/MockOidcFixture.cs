using System.Text.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Olive.Tests;

// UI-01 / D-17: a digest-pinned mock-oauth2-server, started once per test class (IClassFixture, never an assembly fixture) so the token
// auth_time, the factory clock and the real wall clock stay within seconds of each other however late the class runs. One issuer
// ("entra") keeps discovery, JWKS, the token endpoint and iss identical between sign-in and callback; per-flow claim variation is
// selected by a "scenario" token-request parameter that the test-only ScenarioTokenRequestHandler appends.
public sealed class MockOidcFixture : IAsyncLifetime
{
    // Re-check with: docker buildx imagetools inspect ghcr.io/navikt/mock-oauth2-server:6.1.3
    public const string Image = "ghcr.io/navikt/mock-oauth2-server@sha256:2a813381bf282da46ff36ff386059e7903e25865056af8149f4fb3f1441916b8";
    public const string Issuer = "entra";
    public const string ClientId = "olive-test";
    public const string Tenant = "bbbbbbbb-0000-4000-8000-000000000001";
    public const string ReviewerOid = "aaaaaaaa-0000-4000-8000-000000000001";
    public const string OtherReviewerOid = "aaaaaaaa-0000-4000-8000-000000000002";
    public const long StaleAuthTime = 1000000000;
    public const string AlphaGroup = "tenant-alpha-reviewers";

    private IContainer? _container;

    // Real wall clock, captured immediately before JSON_CONFIG is built and the container starts (per fixture instance).
    public DateTimeOffset T0 { get; private set; }

    public string Authority { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        T0 = DateTimeOffset.UtcNow;
        _container = new ContainerBuilder(Image)
            .WithPortBinding(8080, true)
            .WithEnvironment("JSON_CONFIG", BuildConfig(T0))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/" + Issuer + "/.well-known/openid-configuration")))
            .Build();
        await _container.StartAsync();
        Authority = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}/{Issuer}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string BuildConfig(DateTimeOffset t0)
    {
        var fresh = t0.ToUnixTimeSeconds();
        var mappings = new JsonArray
        {
            Mapping("role-present", Claims(groups: [AlphaGroup], roles: ["case_supervisor"])),
            Mapping("role-absent", Claims(groups: [AlphaGroup])),
            Mapping("overage", Claims(extra: new JsonObject { ["hasgroups"] = true, ["_claim_names"] = new JsonObject { ["groups"] = "src1" } })),
            Mapping("no-tenant", Claims(groups: ["unmapped-group"], roles: ["case_supervisor"])),
            Mapping("two-tenant", Claims(groups: [AlphaGroup, "tenant-beta-reviewers"], roles: ["case_supervisor"])),
            Mapping("fresh", Claims(groups: [AlphaGroup], roles: ["case_supervisor"], authTime: fresh)),
            Mapping("fresh-role-absent", Claims(groups: [AlphaGroup], authTime: fresh)),
            Mapping("fresh-other-reviewer", Claims(groups: [AlphaGroup], roles: ["case_supervisor"], authTime: fresh, oid: OtherReviewerOid)),
            Mapping("stale-auth-time", Claims(groups: [AlphaGroup], roles: ["case_supervisor"], authTime: StaleAuthTime)),
        };
        var root = new JsonObject
        {
            ["interactiveLogin"] = false,
            ["tokenCallbacks"] = new JsonArray
            {
                new JsonObject { ["issuerId"] = Issuer, ["tokenExpiry"] = 3600, ["requestMappings"] = mappings },
            },
        };
        return root.ToJsonString();
    }

    private static JsonObject Mapping(string scenario, JsonObject claims) =>
        new() { ["requestParam"] = "scenario", ["match"] = scenario, ["claims"] = claims };

    private static JsonObject Claims(string[]? groups = null, string[]? roles = null, long? authTime = null, string oid = ReviewerOid, JsonObject? extra = null)
    {
        var claims = new JsonObject
        {
            ["sub"] = "sub-" + oid,
            ["oid"] = oid,
            ["tid"] = Tenant,
            ["preferred_username"] = "reviewer@example.test",
            ["aud"] = ClientId,
        };
        if (groups is not null) { claims["groups"] = new JsonArray(groups.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()); }
        if (roles is not null) { claims["roles"] = new JsonArray(roles.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()); }
        if (authTime is { } at) { claims["auth_time"] = at; }
        if (extra is not null)
        {
            foreach (var (key, value) in extra.ToList())
            {
                extra.Remove(key);
                claims[key] = value;
            }
        }

        return claims;
    }
}

// The scenario the next token request will select. Tests set it before completing each callback.
public sealed class MockScenario
{
    private volatile string _current = "role-present";

    public string Current => _current;

    public void Set(string scenario) => _current = scenario;

    // Test-only: the raw tokens last returned by the mock, so sentinel scans can look for them in logs, HTML and cookies.
    public string? LastIdToken { get; set; }

    public string? LastAccessToken { get; set; }
}

// Test-only: appends scenario=<current> to the form body of POSTs to the token endpoint (and nothing else).
public sealed class ScenarioTokenRequestHandler(MockScenario scenario, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var replacement = new StringContent(body + "&scenario=" + Uri.EscapeDataString(scenario.Current), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            request.Content = replacement;
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (request.RequestUri is { } tokenUri && tokenUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) && response.IsSuccessStatusCode)
        {
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            scenario.LastIdToken = doc.RootElement.TryGetProperty("id_token", out var id) ? id.GetString() : null;
            scenario.LastAccessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        }

        return response;
    }
}
