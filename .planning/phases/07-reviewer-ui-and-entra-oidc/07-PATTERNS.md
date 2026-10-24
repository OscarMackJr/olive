# Phase 7: Reviewer UI and Entra OIDC - Pattern Map

**Mapped:** 2026-10-24
**Files analyzed:** 24 new/modified
**Analogs found:** 22 / 24

Note: CLAUDE.md's stack section is stale. The real code is a multi-project solution (Olive.Host, Application, Persistence, Catalog, Integrations.*) using minimal-API endpoints, Dapper over `TenantUnitOfWork`, and Testcontainers fixtures. There are no Razor Pages yet, so the page files have only a partial analog in `QueueEndpoints`.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `src/Olive.Host/Program.cs` (modify) | config/composition | request-response | itself (lines 84-100) | exact |
| `src/Olive.Host/Auth/ReviewerCookie.cs` (new) | middleware/config | request-response | `Auth/DevReviewerAuthenticationHandler.cs` + `ReviewerClaims.cs` | role-match |
| `src/Olive.Host/Auth/ReviewerOidc.cs` (new) | provider/config | request-response | `DevReviewerAuthenticationHandler.cs` (claim emission) | partial |
| `src/Olive.Host/Auth/DecisionFreshness.cs` (new) | service | request-response | `DevReviewerAuthenticationHandler.HandleAuthenticateAsync` (identity re-read + tenant map) | role-match |
| `src/Olive.Host/Auth/DraftStore.cs` (new) | store | CRUD (in-memory, TTL) | `Olive.Application/CachingTraceResolver.cs` (singleton in-memory, injected `TimeProvider`) | partial |
| `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs` (modify) | middleware | request-response | itself, lines 22-26 | exact |
| `src/Olive.Host/Auth/ReviewerClaims.cs` (modify) | utility | transform | itself | exact |
| `src/Olive.Integrations.Entra/EntraClaimsMapper.cs` (new) | utility | transform | `FixtureReviewerIdentitySource.cs` + `ReviewerClaims.ToPrincipal` | role-match |
| `src/Olive.Host/Pages/Review/Index.cshtml(.cs)` (new) | controller/view | request-response | `Endpoints/QueueEndpoints.cs` ListAsync | role-match |
| `src/Olive.Host/Pages/Review/Item.cshtml(.cs)` (new) | controller/view | request-response (CRUD) | `QueueEndpoints.cs` Get/Payload/Claim/Release/Decision | role-match |
| `src/Olive.Host/Pages/Review/Audit.cshtml(.cs)` (new) | controller/view | request-response (read) | `QueueEndpoints.EventsAsync` | role-match |
| `src/Olive.Host/Pages/Review/Forbidden.cshtml` (new) | view | request-response | `QueueEndpoints` filter (lines 16-25) | partial |
| `src/Olive.Host/Pages/Dev/SignIn.cshtml(.cs)` (new) | controller | request-response | `DevReviewerAuthenticationHandler` | role-match |
| `src/Olive.Host/Startup/OidcConfig.cs` (new) | config | transform | `Olive.Domain/SessionKey.cs` + `Program.cs` `ReadTimeoutMs` | role-match |
| `src/Olive.Host/Startup/StartupGuards.cs` (modify) | config/guard | request-response | `CheckDevReviewerNotInProduction` (75-89) | exact |
| `src/Olive.Host/Startup/StartupGuardFilter.cs` (modify) | config | - | itself, lines 11-18 | exact |
| `src/Olive.Application/IAuditStore.cs` + `src/Olive.Persistence/PostgresAuditStore.cs` (new) | store | CRUD (read) | `IExecutionStore` / `PostgresExecutionStore` + `ExecutionSql` | exact |
| `src/Olive.Contracts/ProblemReasons.cs` (modify) | model | - | itself | exact |
| `src/Olive.Host/wwwroot/olive.css` (new) | static asset | - | none | none |
| `tests/Olive.Tests/MockOidcFixture.cs` (new) | test fixture | container lifecycle | `PostgresFixture.cs` | exact |
| `tests/Olive.Tests/OidcFlowDriver.cs` (new) | test helper | request-response | `ReviewerAuthTests.cs` helpers | partial |
| `tests/Olive.Tests/OliveApiFactory.cs` (modify) | test fixture | - | itself (`extraConfig`, `CreateReviewerClient`) | exact |
| `tests/Olive.Tests/{EntraClaimsMapper,ReviewerPages*,DecisionTimeRole,OidcReauth,AuditPage,CredentialRouteMatrix,UiSurface,ReviewerSession,SentinelUi}Tests.cs` (new) | test | request-response | `ReviewerAuthTests.cs`, `StartupGuardTests.cs`, `SentinelScan.cs` | role-match |
| `tests/Olive.Tests/ArchitectureTests.cs` (modify) | test | - | itself, lines 30-35 | exact |
| `Directory.Packages.props`, `Olive.Host.csproj`, `packages.lock.json`, `.env.example` (modify) | config | - | existing Npgsql entries | exact |

## Pattern Assignments

### `src/Olive.Host/Program.cs` (modify: composition)

**Analog:** itself. Current auth wiring is at lines 84-91 and pipeline at 93-100.

```csharp
builder.Services.AddAuthentication(AgentKeyAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevReviewerAuthenticationHandler>(DevReviewerAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddSingleton<IReviewerIdentitySource>(_ => FixtureReviewerIdentitySource.CreateDefault());
builder.Services.AddAuthorization();
...
var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapProposalEndpoints();
app.MapQueueEndpoints();
app.MapCatalogEndpoints();
```

Rules to follow:
- Line 16 forces `EnvironmentName = Production`, so use `StartupGuards.ResolveEnvironment(builder.Configuration)` (the `OliveEnvironment`) for the "non-production only" decisions (DevReviewer scheme, `/dev/sign-in`), never `IWebHostEnvironment`.
- Config-reading style is `builder.Configuration["OLIVE_..."]`, with eager validation before Build (see `ReadTimeoutMs`, lines 102-117, static messages that name the setting and never the value).
- Add `UseStaticFiles`, a CSP header middleware, `AddRazorPages`, `MapRazorPages` and `AddDataProtection`. Keep `app.UseAuthentication()` before `UseAuthorization()`.
- Keep `public partial class Program;` (line 119) for `WebApplicationFactory`.
- Conditional DevReviewer registration: `CheckDevReviewerNotInProduction` stays as a backstop.

---

### `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs` (modify)

**Analog:** itself, lines 22-26. Change the skip rule so the default scheme does nothing outside agent route groups (Pitfall 8).

```csharp
// D-02: reviewer routes belong to the DevReviewer scheme; an agent key there is never introspected (T-05-23).
if (Request.Path.StartsWithSegments("/v1/queue", StringComparison.OrdinalIgnoreCase))
{
    return AuthenticateResult.NoResult();
}
```

Change this to return `NoResult` unless the path is under `/v1/proposals` or `/v1/catalog`. Keep the existing `HandleChallengeAsync` caller separation (lines 43-52) as is.

---

### `src/Olive.Host/Auth/ReviewerClaims.cs` (modify) and cookie/OIDC principal construction

**Analog:** `src/Olive.Host/Auth/ReviewerClaims.cs` lines 6-35. This is the single claims contract. Both the dev-cookie picker and `OnTokenValidated` must produce principals through `ToPrincipal`, and the page code reads them with `ToReviewerContext()`.

```csharp
public const string Oid = "olive:reviewer_oid";
public const string Tid = "olive:reviewer_tid";
public const string Group = "olive:group";
public const string AuthSource = "olive:auth_source";
public const string TenantId = OliveClaims.TenantId;

public static ClaimsPrincipal ToPrincipal(ReviewerIdentity identity, Guid? tenantId, string authSource, string scheme)
{
    var claims = new List<Claim> { new(Oid, identity.Oid), new(Tid, identity.Tid), new(AuthSource, authSource) };
    claims.AddRange(identity.Groups.Select(g => new Claim(Group, g)));
    if (tenantId is { } tenant) { claims.Add(new Claim(TenantId, tenant.ToString("D"))); }
    return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
}

public static ReviewerContext? ToReviewerContext(this ClaimsPrincipal user)
{
    if (!Guid.TryParse(user.FindFirstValue(TenantId), out var tenant)) { return null; }
    return new ReviewerContext(tenant, user.FindFirstValue(Oid) ?? "", user.FindFirstValue(Tid) ?? "",
        user.FindAll(Group).Select(c => c.Value).ToArray(), user.FindFirstValue(AuthSource) ?? "");
}
```

Add only these extra claim constants, as RESEARCH allows: `olive:auth_time`, `olive:session_start`, `olive:reviewer_id` (stub only), plus an optional `olive:no_tenant_reason`. `ToReviewerContext()` returning null is the signal for the D-04 403 page.

---

### `src/Olive.Host/Auth/ReviewerCookie.cs`, `DecisionFreshness.cs` (new)

**Analog:** `src/Olive.Host/Auth/DevReviewerAuthenticationHandler.cs`. Copy the identity lookup, tenant derivation and "never log header or id" discipline.

**Imports and shape** (lines 1-21):
```csharp
using Microsoft.AspNetCore.Authentication;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;
namespace Olive.Host.Auth;
public sealed class DevReviewerAuthenticationHandler(..., IReviewerIdentitySource identities, IReviewerTenantMap tenantMap) : AuthenticationHandler<...>
{
    public const string SchemeName = "DevReviewer";
```

**Tenant derivation to reuse verbatim for cookie issue (dev picker) and the stub fixture re-read at decide (D-10)** (lines 30-40):
```csharp
var identity = values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? identities.Find(values[0]!) : null;
...
// D-08: the tenant claim exists only when the server-side map yields exactly one tenant.
var tenants = tenantMap.TenantsFor(identity.Groups).Distinct().ToArray();
Guid? tenant = tenants.Length == 1 ? tenants[0] : null;
var principal = ReviewerClaims.ToPrincipal(identity, tenant, QueueStates.Stub, Scheme.Name);
```

`DecisionFreshness` (stub path): read `olive:reviewer_id` from the cookie, call `identities.Find(...)` again, rebuild `ReviewerContext` with `QueueStates.Stub` from the current fixture groups. Return null or fail closed if the fixture is gone. The Entra path checks `olive:auth_time` against `TimeProvider` (inject `TimeProvider` as in `AgentKeyAuthenticationHandler`'s constructor, line 16) and the 5-minute constant `MaxFreshness`.

**Challenge and forbidden semantics to mirror for the cookie scheme** (lines 43-57): the existing handler writes `ProblemResults.WriteAsync(Context, 403/401, ProblemReasons.*)`. The cookie scheme instead redirects (302) for browsers, which is by design (D-16 matrix: no cookie on `/review` gives 302 to sign-in). The `/v1/*` handlers stay JSON.

---

### `src/Olive.Integrations.Entra/EntraClaimsMapper.cs` (new: pure transform)

**Analog:** `src/Olive.Integrations.Entra/FixtureReviewerIdentitySource.cs`. It lives in the same project, returns the same `ReviewerIdentity` record, and has the same strict-parse style.

```csharp
namespace Olive.Integrations.Entra;
// returns ReviewerIdentity(ReviewerId, Oid, Tid, Upn, Groups) from Olive.Application
var identity = new ReviewerIdentity(root.GetProperty("reviewer_id").GetString()!, root.GetProperty("oid").GetString()!,
    root.GetProperty("tid").GetString()!, root.GetProperty("upn").GetString()!, groups);
```

Rules (from RESEARCH Pattern 2): raw claim names (`MapInboundClaims=false`), require `oid` and `tid` to be GUIDs, drop `role:`-prefixed `groups` values, accept `roles` only through an allowlist, detect overage (`hasgroups` or `_claim_names` containing `groups`), return a static reason code. Use `FixtureReviewerIdentitySource`'s `throw new InvalidOperationException("<static message>")` style, never including claim values. Keep this project free of `System.Net.Http` (REQ_ACT_04).

The tenant comes from `IReviewerTenantMap.TenantsFor(groups).Distinct()`, exactly as in the dev handler above. Tests for the mapper are pure unit tests, no Postgres.

---

### `src/Olive.Host/Startup/OidcConfig.cs` (new: sealed redacting secret + env parse)

**Analog:** `src/Olive.Domain/SessionKey.cs` (full file, 28 lines). Copy it for `OidcClientSecret`.

```csharp
[DebuggerDisplay("[REDACTED]")]
[JsonConverter(typeof(RedactingJsonConverter<SessionKey>))]
public sealed class SessionKey : IFormattable
{
    private readonly string _value;
    public SessionKey(string value) { if (string.IsNullOrEmpty(value)) throw new ArgumentException("A session key must not be empty."); _value = value; }
    public string ExposeSecret() => _value;
    public override string ToString() => "[REDACTED]";
    public string ToString(string? format, IFormatProvider? formatProvider) => "[REDACTED]";
}
```

Place the secret type in `Olive.Domain` (next to `SessionKey`, with its own `RedactingJsonConverter<T>`) so `SecretTypeTests.cs` can cover it the same way. Config read style: `configuration["OLIVE_OIDC_AUTHORITY"]`, with messages that name the setting and never the value (`Program.cs` lines 103-117).

---

### `src/Olive.Host/Startup/StartupGuards.cs` + `StartupGuardFilter.cs` (modify)

**Analog:** `CheckDevReviewerNotInProduction` (StartupGuards lines 75-89) and `CheckExecutorOutput` (91-121) for config-missing guards.

```csharp
public static void CheckDevReviewerNotInProduction(IServiceProvider provider, OliveEnvironment env)
{
    if (!env.IsProduction) { return; }
    var scheme = provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(Auth.DevReviewerAuthenticationHandler.SchemeName).GetAwaiter().GetResult();
    if (scheme is not null) { throw new StartupGuardException("Q17", "the dev reviewer authentication scheme is registered in production"); }
}
// config guard style:
const string reason = "the executor output path is missing or not writable";
if (string.IsNullOrWhiteSpace(path)) { throw new StartupGuardException("EXE-04", reason); }
```

Add `CheckOidcConfiguration(IConfiguration, OliveEnvironment)` (production only: missing authority, client id or secret, or non-https authority) and a dev-sign-in-page check. `StartupGuardFilter.Configure` (lines 11-18) is a flat list of `StartupGuards.CheckX(...)` calls, so add the new calls there. Messages are static, with a requirement-id code string (`"UI-01"`). FND-05 already bars any `INonProductionOnly` type in production, so mark the picker page model or its helper `INonProductionOnly` (`Olive.Application/INonProductionOnly.cs`).

Test analog for guards: `tests/Olive.Tests/StartupGuardTests.cs` (lines ~208-266). It uses `OliveApiFactory(db, environment: ...)`, `TryStartAsync(factory)`, and asserts `Assert.Contains("Q17", guard.Message)`. For a "no secret value in message" assertion, copy the same style.

---

### `src/Olive.Host/Pages/Review/*.cshtml(.cs)` (Razor Pages, new)

**Analog:** `src/Olive.Host/Endpoints/QueueEndpoints.cs`. No Razor Pages exist. Copy the QueueService calling convention, result-switch mapping and tenant gate, and translate each `ProblemReasons` arm to a flash code instead of a ProblemDetails.

**Tenant gate** (lines 16-25), which becomes the D-04 403 page:
```csharp
if (ctx.HttpContext.User.ToReviewerContext() is null)
{
    return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.ReviewerTenantUnresolved);
}
```

**Result-switch with an explicit default arm** (lines 90-102, claim):
```csharp
var result = await service.ClaimAsync(http.User.ToReviewerContext()!, id, ct);
return result switch
{
    ClaimResult.Claimed c => ...,
    ClaimResult.AlreadyClaimed => ProblemResults.Result(409, ProblemReasons.AlreadyClaimed),
    ClaimResult.ProposalExpired => ... ProblemReasons.ProposalExpired,
    ClaimResult.Closed => ... ProblemReasons.ItemClosed,
    ClaimResult.NotFound => ProblemResults.Result(404, ProblemReasons.ItemNotFound),
    _ => throw new InvalidOperationException("Unknown claim result."),
};
```

**Decision arms and post-approval execution** (lines 149-168 and 173-197): the page POST handler must call `service.DecideAsync(reviewer, id, decision, rationale, ct)`, then the same `ExecuteAfterApprovalAsync` logic (Phase 6 D-01). Extract `ExecuteAfterApprovalAsync` into a shared internal helper rather than copying it (it is currently `private static` in `QueueEndpoints`). Keep `CancellationToken.None` and the static log messages (`"Simulated execution failed after approval. proposal_id={ProposalId} tenant_id={TenantId}"`).

```csharp
DecisionResult.RoleNotPermitted => 403 role_not_permitted     // UI: 403 page with role_not_held (D-09), item stays claimed
DecisionResult.SelfApproval => 403 SelfApprovalForbidden
DecisionResult.Unresolved => 403 ApprovalIdentityUnresolved
DecisionResult.Unavailable => 503 Retryable IdentityResolutionUnavailable
DecisionResult.Conflict / NotClaimant / NotClaimed / Closed / ProposalExpired / NotFound / KindNotSupported / InvalidRequest
```

Payload gating arms (lines 67-77): `PayloadResult.Found` / `NotFound` / `RoleNotPermitted`. In the UI, `RoleNotPermitted` means "show only item refs", not an error (D-12). Events (lines 79-88): `EventsResult.Found f` gives `f.Events` with `Seq, EventType, FromState, ToState, Actor, CreatedAt, DetailJson`. Render `DetailJson` encoded and only if static.

`ParseJson` (lines 199-204) is the pattern for parsing the parameters JSON for the key/value table; render values through Razor encoding.

Conventions to keep: `{id:guid}` route constraint (line 27), explicit default arm in every switch, no logging of rationale/parameters. Page models take `QueueService` by constructor. They have no logic path of their own (D-01). Use `[ValidateAntiForgeryToken]` default behaviour of Razor Pages and end every POST with `RedirectToPage`.

Folder authorization: `Conventions.AuthorizeFolder("/Review", "ReviewerUi")` with a policy built the same way as the existing route group at `QueueEndpoints` line 15:
```csharp
.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DevReviewerAuthenticationHandler.SchemeName })
```
For the UI, use `AuthenticationSchemes = ReviewerCookie.Scheme`. Do not name the cookie scheme in any `/v1/*` policy.

---

### `src/Olive.Application/IAuditStore.cs` + `src/Olive.Persistence/PostgresAuditStore.cs` + `AuditSql.cs` (new)

**Analog:** `PostgresExecutionStore` + `ExecutionSql` (read-only methods). `ListApprovedAsync` (PostgresExecutionStore lines 49-51) is the shape to copy:

```csharp
public Task<IReadOnlyList<Guid>> ListApprovedAsync(Guid tenantId, CancellationToken ct) =>
    uow.ExecuteAsync<IReadOnlyList<Guid>>(tenantId, async (conn, tx) =>
        (await conn.QueryAsync<Guid>(new CommandDefinition(ExecutionSql.ListApproved, new { tenant_id = tenantId }, tx, cancellationToken: ct))).ToList(), ct);
```

Dapper private row classes with `DefaultTypeMap.MatchNamesWithUnderscores = true` in a static constructor (lines 12, 20-36), `Utc(DateTime)` converter (line 38), and the `using Dapper; using Npgsql; using Olive.Application; using Olive.Domain;` block (lines 1-4). The class is `public sealed class X(TenantUnitOfWork uow) : IX`.

**SQL conventions** (`ExecutionSql.cs`): `public static class`, `public const string`, always `where tenant_id = @tenant_id and proposal_id = @proposal_id`, cast jsonb with `parameters::text as parameters_json`. Example:
```csharp
public const string LoadProposal =
    "select state, action_type, parameters::text as parameters_json, expires_at from proposal where tenant_id = @tenant_id and proposal_id = @proposal_id";
```
The audit select needs `party_ref, justification, body_sha256, proposal_hash, trace_verification` from `proposal` (columns confirmed in `ProposalSql.cs` line 7) and the `execution_chain` row `where tenant_id = @tenant_id and proposal_id = @proposal_id`. The view (sql/migrations/0009_execution_ledger.sql lines 36-60) exposes `policy_decision, decided_by, auth_source, decided_at, rationale, execution_mode, started_at, outcome, ...`. The view is `security_invoker`, so RLS applies. Keep the application tenant predicate (project rule).

**Hash recompute:** `Olive.Domain.Jcs.ProposalHash(JsonElement root)` (Jcs.cs line 58) hashes the canonical proposal fields. Build a JSON object from the stored fields (see `CanonicalizeProposalFields`, line 22, for the expected property names) and compare with ordinal equality to the stored `proposal_hash`.

Interface lives in `Olive.Application` (the ports live there, e.g. `IExecutionStore.cs`, `IProposalStore.cs`). Do not add HTTP types.

---

### `src/Olive.Contracts/ProblemReasons.cs` (modify)

**Analog:** itself, lines 20-22. Add `role_not_held`, `groups_overage`, `no_reviewer_tenant` as `public const string` snake_case codes next to the existing `ReviewerTenantUnresolved = "reviewer_tenant_unresolved"` and `RoleNotPermitted = "role_not_permitted"`. `tests/Olive.Tests/ProblemReasonTitleTests.cs` likely requires a title for every reason; update it if so (check before adding).

---

### `tests/Olive.Tests/MockOidcFixture.cs` (new)

**Analog:** `tests/Olive.Tests/PostgresFixture.cs`.

```csharp
using Testcontainers.PostgreSql;
using Xunit;
[assembly: AssemblyFixture(typeof(Olive.Tests.PostgresFixture))]
namespace Olive.Tests;
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder(PostgresImage).Build();
        await _container.StartAsync();
        _host = _container.Hostname;
        _port = _container.GetMappedPublicPort(5432);
```
Register the new fixture with a second `[assembly: AssemblyFixture(typeof(...))]` and inject it via test constructors the same way (`ReviewerAuthTests(PostgresFixture fixture)`). Use `ContainerBuilder` with the digest-pinned image from RESEARCH. xunit.v3 uses `ValueTask InitializeAsync/DisposeAsync`. Tests that need Docker carry `[Trait("Category", "Integration")]` (ReviewerAuthTests line 9).

---

### `tests/Olive.Tests/OliveApiFactory.cs` (modify) and page/auth tests

**Analog:** itself. Config injection via the `extraConfig` constructor argument (lines 23, 69-80) and `environment` ("ci" default). Add the OIDC settings via `extraConfig` (`OLIVE_OIDC_AUTHORITY`, `OLIVE_OIDC_CLIENT_ID`, `OLIVE_OIDC_CLIENT_SECRET`), or add an overload. Note that Program.cs reads some settings before Build, so pre-Build settings must also go through `builder.UseSetting(...)` (lines 78-85). Settings consumed by `AddOpenIdConnect` options delegates at Build time are fine through `ConfigureAppConfiguration`, but anything read in top-level Program.cs needs `UseSetting`.

Client helpers (lines 152-157):
```csharp
public HttpClient CreateReviewerClient(string reviewerId)
{
    var client = CreateClient();
    client.DefaultRequestHeaders.Add(Olive.Host.Auth.DevReviewerAuthenticationHandler.HeaderName, reviewerId);
    return client;
}
```
Add `CreateUiClient()` with `WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true }`, plus a dev-cookie client that POSTs `/dev/sign-in` (needs the antiforgery token scrape).

**Test structure to copy** (ReviewerAuthTests lines 11-35):
```csharp
[Trait("Category", "Integration")]
public sealed class ReviewerAuthTests(PostgresFixture fixture)
{
    [Fact]
    public async Task APR_01_valid_reviewer_authenticates_with_stub_auth_source()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateReviewerClient("alpha-sup-1");
        var response = await client.GetAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
```
Naming: `<REQ_ID>_<behaviour>` (`APR_01_...`, `FND_05_...`); use `UI_07_...`, `APR_03_...`. Always pass `TestContext.Current.CancellationToken`. The credential x route matrix (UI-07) copies the caller-separation tests (lines 49-90): they already assert `403 principal_not_permitted`, `401 reviewer_unauthenticated`, `401 key_inactive`, and `Assert.Equal(before, factory.Introspector.Calls)` (agent key never introspected on reviewer routes). Extend to `/review` and `/dev/sign-in`. `ReasonAsync` helper (lines 14-18) parses the `reason` field.

The test-only probe middleware pattern (`ReviewerProbeStartupFilter`, OliveApiFactory lines 160-190) shows how a test-only endpoint is added without touching `src`. Use the same pattern if a probe for the cookie scheme is needed.

Sentinel tests: `tests/Olive.Tests/SentinelScan.cs` (`AddSource(name, text)`, `AddResponseAsync(name, res)`, `AddJsonl`) with `factory.Logs` (`InMemoryLogCapture`, Trace level). Add the rationale sentinel, id_token/code strings and client secret as sources, and add HTML bodies from the page responses. Time-dependent tests use `ManualTimeProvider.cs`.

---

### `tests/Olive.Tests/ArchitectureTests.cs` (modify)

**Analog:** itself, lines 30-35:
```csharp
private static readonly Dictionary<string, string[]> PackageAllowlist = new()
{
    ["Olive.Persistence"] = ["Dapper", "Npgsql"],
    ["Olive.Catalog"] = ["JsonSchema.Net", "YamlDotNet"],
    ["Olive.Host"] = ["Npgsql"],
};
```
Change to `["Olive.Host"] = ["Microsoft.AspNetCore.Authentication.OpenIdConnect", "Npgsql"]`. The assertion (lines 141-142) compares sorted direct package references. Also add `<PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" />` to `src/Olive.Host/Olive.Host.csproj` (existing item group lines 14-16 holds `<PackageReference Include="Npgsql" />`), `PackageVersion` in `Directory.Packages.props`, and regenerate `src/Olive.Host/packages.lock.json` (CI uses locked mode). The `UiSurface` architecture test (UI-06) follows the same reflection/file-scan style as the rest of this file.

---

### `.env.example` (modify)

**Analog:** itself. Each optional setting is a commented line with a one-line description. Add `OLIVE_OIDC_AUTHORITY`, `OLIVE_OIDC_CLIENT_ID`, `OLIVE_OIDC_CLIENT_SECRET=` (empty placeholder, never a real secret) and `OLIVE_DATA_PROTECTION_PATH`.

---

## Shared Patterns

### Static reason codes, never echo input
**Source:** `src/Olive.Contracts/ProblemReasons.cs`, `src/Olive.Host/Problems/ProblemResults.cs`
**Apply to:** every page flash, 403 page, guard message, OIDC failure handler. Flash holds only a reason code. The rationale, parameters, claim values, tokens and exception text never appear in HTML, TempData, logs, or guard messages.

### Tenant only from the server-side map
**Source:** `DevReviewerAuthenticationHandler.cs` lines 36-39 and `QueueEndpoints.cs` lines 16-25. Never from header, form or query.
**Apply to:** the picker, `OnTokenValidated`, `DecisionFreshness`, all pages (`User.ToReviewerContext()` null gives a 403 page).

### Result switch with explicit default arm
**Source:** `QueueEndpoints.cs` (every `result switch`, `_ => throw new InvalidOperationException("Unknown ... result.")`). Apply to all page handlers.

### Time injected
**Source:** `AgentKeyAuthenticationHandler` constructor takes `TimeProvider time`, and `Program.cs` line 23 registers `TimeProvider.System`. Apply to the session cap, freshness window and draft TTL so tests use `ManualTimeProvider`.

### Tenant predicate plus forced RLS on every query
**Source:** `ExecutionSql.cs`, `TenantUnitOfWork.ExecuteAsync(tenantId, ...)`. Apply to the audit read.

### No HTTP client in src
`BannedSymbols.txt` and the `REQ_ACT_04` test forbid `System.Net.Http` references in src. The OIDC handler's own backchannel is not Olive code, so do not touch `Backchannel*` options. Test code may use `HttpClient`.

### Startup guard shape
**Source:** `StartupGuards.cs` + `StartupGuardFilter.cs` (see above). Static messages, requirement-id prefix, called from the filter before Kestrel listens.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `Pages/**/*.cshtml`, `Pages/Shared/_Layout.cshtml`, `wwwroot/olive.css` | view | request-response | No Razor views or static assets exist; use RESEARCH Pattern 3/4 and 07-UI-SPEC.md |
| `Auth/ReviewerOidc.cs` (OpenIdConnect options and events) | provider | request-response | No OIDC or cookie handler exists; use RESEARCH Pattern 1 and Code Examples. Reuse only `ReviewerClaims.ToPrincipal` for the output |
| `Auth/DraftStore.cs` | store | in-memory TTL | Closest is `CachingTraceResolver` (singleton in-memory), not a TTL single-use store; follow RESEARCH Pitfall 10 |
| `tests/Olive.Tests/OidcFlowDriver.cs` | test helper | redirect-driving | No existing cookie/redirect driver; RESEARCH Pattern 8 steps (https base address, `AllowAutoRedirect=false`) |

## Metadata

**Analog search scope:** `src/Olive.Host`, `src/Olive.Application`, `src/Olive.Persistence`, `src/Olive.Integrations.Entra`, `src/Olive.Domain`, `tests/Olive.Tests`, `sql/migrations`, `config`
**Files scanned:** about 25 read in full or in part, about 150 listed
**Pattern extraction date:** 2026-10-24
