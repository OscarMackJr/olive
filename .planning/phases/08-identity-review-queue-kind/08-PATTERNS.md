# Phase 8: Identity Review Queue Kind - Pattern Map

**Mapped:** 2026-10-25
**Files analyzed:** 30 (new or modified)
**Analogs found:** 28 / 30

Line numbers refer to the repo at HEAD 23d8eeb. RESEARCH.md (08-RESEARCH.md) already holds the SQL, migration text, wire shapes and claim names. This file points the planner at the real code to copy from, so it does not repeat them.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `src/Olive.Host/Auth/ServiceTokenAuthenticationHandler.cs` (new) | middleware (auth handler) | request-response | `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs` | exact |
| `src/Olive.Host/Auth/QueueCallerFilters.cs` + `ServiceClaims` (new) | middleware (endpoint filter) | request-response | inline filter in `QueueEndpoints.cs:16-25`; `src/Olive.Host/Auth/OliveClaims.cs`, `ReviewerClaims.cs` | role-match |
| `src/Olive.Host/Endpoints/QueueEndpoints.cs` (modify) | controller (minimal API) | request-response | itself (lines 12-180), `ProposalEndpoints.cs` for the body-read order | exact |
| `src/Olive.Application/IServiceTokenVerifier.cs`, `ServiceTokenResult.cs` (new) | port | request-response | `IKeyIntrospector` / `IntrospectionResult` (not read; same assembly) | exact |
| `src/Olive.Domain/InboundBearerToken.cs` (new) | model (redacting secret) | n/a | `src/Olive.Domain/SessionKey.cs` | exact |
| `src/Olive.Integrations.Popeye/StubServiceTokenVerifier.cs` (new, plus csproj EmbeddedResource) | service (fixture stub) | request-response | `src/Olive.Integrations.Popeye/StubKeyIntrospector.cs` | exact |
| `contracts/service-token/*` (new fixtures, schema, valid/invalid) | config / contract | n/a | `contracts/introspection/` | exact |
| `config/producers.yaml` + `src/Olive.Catalog/ProducerRegistry.cs`, `IProducerRegistry` (new) | config registry | batch (startup) | `config/agents.yaml` + `src/Olive.Catalog/AgentAllowlist.cs` | exact |
| `src/Olive.Host/Startup/StartupGuards.cs` + `StartupGuardFilter.cs` (modify: `CheckProducers`) | utility (startup guard) | batch | `StartupGuards.CheckQueueKinds` (lines 44-73) | exact |
| `src/Olive.Host/Program.cs` (modify: scheme, stub, registry) | config | n/a | existing `AddAuthentication` chain and stub registrations | exact (not re-read) |
| `src/Olive.Application/QueueService.cs` (modify: create, withdraw, poll, identity decide) | service | CRUD | itself (lines 1-176) | exact |
| `src/Olive.Application/ProducerCursor.cs` (new) | utility | transform | none (RESEARCH "Cursor codec") | no analog |
| `src/Olive.Application/IQueueStore.cs` + result records (modify) | port | CRUD | itself | exact |
| `src/Olive.Persistence/PostgresQueueStore.cs` (modify) | store | CRUD, transactional CAS | itself (lines 185-299) | exact |
| `src/Olive.Persistence/QueueSql.cs` (modify) | store (SQL constants) | CRUD | itself | exact |
| `sql/migrations/0010_identity_review_producer.sql` (new) | migration | n/a | `sql/migrations/0008_review_queue.sql`, `0004`/`0005` (not re-read) | role-match |
| `src/Olive.Host/Pages/Review/Index`, `Item`, `Audit` (`.cshtml` + `.cs`) (modify) | component (Razor) | request-response | the same pages | exact |
| `src/Olive.Host/Problems/ProblemReasons.cs`, `ProblemResults.Title`, `FlashMessages` (modify) | utility | request-response | existing entries | exact |
| `contracts/reviewer/alpha-steward-1.json`, `alpha-steward-2.json`, `beta-steward.json`, `index.json` (new/modify) | fixture | n/a | `contracts/reviewer/alpha-sup-1.json` | exact |
| `contracts/queue-item/v1/*` (extend: create, withdraw, poll schemas + pairs) | contract | n/a | `contracts/queue-item/v1/queue-item.schema.json` | exact |
| `contracts/introspection/index.json` (modify: service-token string as `application`) | fixture | n/a | `fixture-key-application` entry | exact |
| `tests/Olive.Tests/CredentialRouteMatrixTests.cs` (modify) | test | request-response | itself (lines 14-110+) | exact |
| `tests/Olive.Tests/IdentityReview*Tests.cs`, `ServiceToken*Tests.cs`, `ProducerRegistryTests.cs`, `ProducerCursorTests.cs` (new) | test | request-response | `QueueEventsTests`, `QueueTableTests`, `AgentAllowlistTests`, `StubKeyIntrospector` tests (names from RESEARCH) | role-match |
| `tests/Olive.Tests/IdentityReviewDemoTests.cs` (new) | test (demo) | request-response | `tests/Olive.Tests/Beat4DemoTests.cs` | exact |
| `build/demo-identity-review.sh` (new) | script | batch | `build/demo-beat4.sh` | exact |
| Existing tests to update (`PrivilegeMatrixTests`, `MigrationRunnerTests`, `ReviewerSessionTests`, `QueueServiceDecideTests`, `ArchitectureTests`, `ContractSchemaTests`, `StartupGuardTests`) | test | n/a | themselves | exact |

## Pattern Assignments

### `src/Olive.Host/Auth/ServiceTokenAuthenticationHandler.cs` (middleware, request-response)

**Analog:** `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs`

**Handler shell and constructor** (lines 1-18):
```csharp
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Olive.Application;
using Olive.Contracts;
using Olive.Host.Problems;

public sealed class AgentKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IKeyIntrospector introspector,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
```

**Route-scope guard, verify, fail closed** (lines 22-43). Copy this, switch the path to `/v1/queue/items`, read the token with a new `InboundBearerToken.TryRead` that has the same rules as `AgentKeyHeader.TryRead`, and call `IServiceTokenVerifier`:
```csharp
if (!Request.Path.StartsWithSegments("/v1/proposals", StringComparison.OrdinalIgnoreCase)
    && !Request.Path.StartsWithSegments("/v1/catalog", StringComparison.OrdinalIgnoreCase))
{
    return AuthenticateResult.NoResult();
}
var key = AgentKeyHeader.TryRead(Request.Headers[AgentKeyHeader.Name].ToString());
if (key is null) { return AuthenticateResult.Fail(ProblemReasons.KeyInactive); }
// IntrospectionUnavailableException is deliberately not caught: it fails closed via the exception handler.
var result = await introspector.IntrospectAsync(key, Context.RequestAborted);
if (!result.Active || result.Key is null || result.Key.ExpiresAt <= time.GetUtcNow())
{
    return AuthenticateResult.Fail(ProblemReasons.KeyInactive);
}
var principal = OliveClaims.ToPrincipal(result.Key, Scheme.Name);
return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
```

**Challenge** (lines 46-57). Copy the 401 and `WWW-Authenticate: Bearer`. Add `HandleForbiddenAsync` that writes 403 `principal_not_permitted`. The AgentKey handler's dev-header to 403 special case is not needed here, because that case is handled by the `QueueCaller` selector.
```csharp
Response.Headers.WWWAuthenticate = AgentKeyHeader.Scheme;
await ProblemResults.WriteAsync(Context, StatusCodes.Status401Unauthorized, ProblemReasons.KeyInactive);
```

Do NOT widen `AgentKeyAuthenticationHandler`'s path scope. It must keep returning `NoResult` on `/v1/queue` (the matrix asserts the introspector call count is unchanged there).

### `src/Olive.Host/Auth/QueueCallerFilters.cs` and `ServiceClaims` (middleware, request-response)

**Analog:** the inline group filter in `src/Olive.Host/Endpoints/QueueEndpoints.cs` lines 14-25, plus `OliveClaims.ToPrincipal` / `ReviewerClaims.ToPrincipal(..., Scheme.Name)` for building a principal with `Scheme.Name` as the authentication type. (`OliveClaims.cs` and `ReviewerClaims.cs` were not read in full. The planner should read them before writing `ServiceClaims`.)

Existing tenant filter to turn into the `ReviewerOnly` filter body (add an authentication-type check in front, per RESEARCH "Claims"):
```csharp
.AddEndpointFilter(async (ctx, next) =>
{
    // D-08: the tenant comes only from the claim set by the server-side group map, never query, header or body.
    if (ctx.HttpContext.User.ToReviewerContext() is null)
    {
        return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.ReviewerTenantUnresolved);
    }
    return await next(ctx);
});
```
Wrong caller type returns `ProblemResults.Result(403, ProblemReasons.PrincipalNotPermitted)`. Expose the checks as public static `IResult?` methods so `ListOrPollAsync` can call them inline (RESEARCH Pattern 1).

### `src/Olive.Host/Endpoints/QueueEndpoints.cs` (controller, request-response)

**Analog:** itself.

**Group declaration to change** (lines 14-15). Replace the scheme name with `QueueCaller.SchemeName` (policy scheme) and move the tenant filter to per-route filters:
```csharp
var group = app.MapGroup("/v1/queue/items")
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DevReviewerAuthenticationHandler.SchemeName })
```

**Result-switch with explicit default arm**, to copy for every new handler (lines 90-102):
```csharp
return result switch
{
    ClaimResult.Claimed c => Results.Ok(ToDto(service.ToView(c.Item))),
    ClaimResult.AlreadyClaimed => ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.AlreadyClaimed),
    ClaimResult.NotFound => ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound),
    _ => throw new InvalidOperationException("Unknown claim result."),
};
```

**Strict JSON body handling for create and withdraw** (`DecisionAsync`, lines 120-147). Copy the shape: content-type check, bounded read with 413, strict parse, string-only values, and `default: return invalid` for unknown fields. Two changes for create. Use `MaxDepth = JsonGate.MaxDepth` plus `JsonGate.Walk(root)` instead of `MaxDepth = 8`. Check the server-assigned set before the unknown-field check, so `server_assigned_field` wins (Pitfall 5).
```csharp
var invalid = ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.InvalidRequest);
if (!http.Request.HasJsonContentType()) { return invalid; }
var bytes = await ProposalEndpoints.ReadBoundedAsync(http.Request, ct);
if (bytes is null) { return ProblemResults.Result(StatusCodes.Status413PayloadTooLarge, ProblemReasons.PayloadTooLarge); }
using var doc = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
```
Wrap the parse in `catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return invalid; }` (lines 144-147).

**Decision endpoint**: add a `DecisionResult.Deferred` arm mapped to `Results.Ok(ToDto(service.ToView(d.Item)))` and an `item_withdrawn` 409 arm. `ApprovalExecution.RunAsync(result, ...)` (line 150) stays as is, because it ignores items without a proposal.

**PayloadAsync**: add a `PayloadResult.NoPayload` arm (404, `no_payload`) before the default arm (lines 70-76).

**DTO**: `ToDto` (lines 178-179) uses a positional constructor. Add `RulesetVersion` as a trailing optional parameter.

### `src/Olive.Domain/InboundBearerToken.cs` (model)

**Analog:** `src/Olive.Domain/SessionKey.cs` lines 1-28. Copy the whole file, rename the type, and add `TryRead(string header)` modelled on `AgentKeyHeader.TryRead`:
```csharp
// FND-06: a sealed class, never a record (a record's generated ToString prints members).
[DebuggerDisplay("[REDACTED]")]
[JsonConverter(typeof(RedactingJsonConverter<SessionKey>))]
public sealed class SessionKey : IFormattable
{
    private readonly string _value;
    public SessionKey(string value) { if (string.IsNullOrEmpty(value)) { throw new ArgumentException("A session key must not be empty."); } _value = value; }
    public string ExposeSecret() => _value;
    public override string ToString() => "[REDACTED]";
    public string ToString(string? format, IFormatProvider? formatProvider) => "[REDACTED]";
}
```
Constraint: it must NOT reuse `ServiceToken` or `ServiceTokenGrant`. `ArchitectureTests` IDN_08 allows those only on Hometown ports (Pitfall 2).

### `src/Olive.Integrations.Popeye/StubServiceTokenVerifier.cs` (service stub)

**Analog:** `src/Olive.Integrations.Popeye/StubKeyIntrospector.cs`

**Class shape and factory** (lines 14-35): primary constructor taking `IReadOnlyDictionary<string,string> tokenToJson, TimeProvider time`, `: IServiceTokenVerifier, INonProductionOnly`, `CreateDefault` reading `service-token/index.json` embedded resources, and `ReadResource` throwing `IntrospectionUnavailableException("malformed_introspection_response")`.
```csharp
public static StubKeyIntrospector CreateDefault(TimeProvider time)
{
    var asm = typeof(StubKeyIntrospector).Assembly;
    var index = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(asm, "introspection/index.json"))
        ?? throw new IntrospectionUnavailableException(Malformed);
    var map = index.ToDictionary(kv => kv.Key, kv => ReadResource(asm, "introspection/" + kv.Value), StringComparer.Ordinal);
    return new StubKeyIntrospector(map, time);
}
```

**Verify body** (lines 37-107). Copy it for the structure: unknown token returns `Inactive`, then `JsonDocument.Parse`, an `active` boolean check, strict `Str`/`OptStr` helpers, a UUID regex for `tenant_id`, `expires_at <= time.GetUtcNow()` returning `Inactive`, and a `catch (JsonException)` that throws the malformed exception. Replace `key_id`, `agent_id` and `on_behalf_of` with `client_id`, `tenant_id` and the optional `delegated_agent_id`. Keep `principal_type` validated with `PrincipalTypes.All.Contains` (line 70). The divergence comment on lines 10-13 applies here too: agent and user fixtures return `active:true`, so Olive's own 403 runs (Pitfall 1).

**csproj**: add `<EmbeddedResource Include="..\..\contracts\service-token\*.json" LogicalName="service-token/%(Filename)%(Extension)" />`, mirroring the existing `introspection/` entry in the Popeye csproj (not read; copy that line).

### `config/producers.yaml` and `src/Olive.Catalog/ProducerRegistry.cs` (config registry)

**Analog:** `src/Olive.Catalog/AgentAllowlist.cs` and `config/agents.yaml`

**YAML shape and header comment** (`config/agents.yaml`):
```yaml
# Agent allowlist (D-15, IDN-02). Source-controlled and change-managed like the catalog.
# Not overridable by environment variable or appsettings.
agents:
  - agent_id: liam
    entitlements: [ent:crm.case_note.write]
```

**Strict parse with a static error and a snake_case convention** (`AgentAllowlist.cs` lines 10-63):
```csharp
private const string Invalid = "Agent allowlist is invalid.";
private sealed class AllowlistDocument { public List<AgentEntry>? Agents { get; set; } }
private sealed class AgentEntry { public string? AgentId { get; set; } public List<string>? Entitlements { get; set; } }
[GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")] private static partial Regex IdPattern();
...
// Strict mapping: IgnoreUnmatchedProperties is deliberately not set.
doc = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build().Deserialize<AllowlistDocument?>(yaml);
...
catch (YamlDotNet.Core.YamlException) { throw new InvalidOperationException(Invalid); }
```
Duplicate client ids and bad ids throw the static message (lines 47-50).

**Embedded load** (lines 65-71): `GetManifestResourceStream("config/producers.yaml")`. The existing `config/*.yaml` glob in `Olive.Catalog.csproj` already embeds the new file.

**Lookup API shape** (lines 73-76): `TryGetByClientId` in the same style as `IsAllowed`, guarding with `string.IsNullOrWhiteSpace`.

### `StartupGuards.CheckProducers` (utility, startup)

**Analog:** `src/Olive.Host/Startup/StartupGuards.cs` lines 44-73 (`CheckQueueKinds`). Copy the shape: resolve services from the provider, throw a bare `InvalidOperationException` on any mismatch, catch `Exception`, and rethrow a `StartupGuardException` with a static rule id and message. Never quote content.
```csharp
catch (Exception)
{
    throw new StartupGuardException("QUE-02", "the review queue kind registry failed validation");
}
```
Wire it into `StartupGuardFilter.cs` line 16, after `StartupGuards.CheckQueueKinds(provider);`.

### `src/Olive.Application/QueueService.cs` (service, CRUD)

**Analog:** itself.

**Class header and conventions** (lines 7-12): primary constructor `(IQueueStore store, IQueueKindRegistry registry, ApprovalIdentityCheck identityCheck, TimeProvider time)`, `DefaultLimit = 100` (reuse for the poll page size, D-16), and business outcomes returned as result records, never thrown.

**Identity branch of decide.** Modify line 76 (`if (item.Kind != QueueStates.ActionApproval || ...) KindNotSupported`). Lines 77-79 (decision in `definition.Decisions`, rationale rules, role check) stay and apply to both kinds. Lines 81-99 (the `identityCheck` block) run only for `action_approval`. For `identity_review`, call a new `store.DecideIdentityAsync`:
```csharp
if (item.Kind != QueueStates.ActionApproval || !registry.TryGet(item.Kind, out var definition)) { return new DecisionResult.KindNotSupported(); }
if (decision is null || !definition.Decisions.Contains(decision, StringComparer.Ordinal)) { return new DecisionResult.InvalidRequest(); }
if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > MaxRationaleLength || rationale.Contains('\0')) { return new DecisionResult.InvalidRequest(); }
if (!reviewer.Groups.Contains(definition.RequiredRole, StringComparer.Ordinal)) { return new DecisionResult.RoleNotPermitted(); }
```

**Result mapping** (lines 101-113). Add `Deferred` and `Withdrawn` arms in the same style, keeping the `_ => throw new InvalidOperationException("Unknown decide result.")` default.

**Payload role-gate ordering** (lines 34-45): keep the `RoleNotPermitted` check first. Then, if `item.ProposalId is null`, return the new `PayloadResult.NoPayload` (Pitfall 3).

**Records** (lines 121-176): result hierarchies are `abstract record X { public sealed record Y(...) : X; }`. Add `CreateResult`, `WithdrawResult` and `PollResult` in this style. `QueueItemView` (line 129) gets a trailing `string? RulesetVersion = null`.

### `src/Olive.Persistence/PostgresQueueStore.cs` (store, transactional CAS)

**Analog:** itself.

**Transaction pattern**: `uow.ExecuteAsync<T>(tenantId, async (conn, tx) => { ... }, ct)`. Take `LockItem` (FOR UPDATE), check state in order, run a guarded UPDATE, check `n != 1` and throw, write the event, then `Reload`. Defer and withdraw copy `ReleaseAsync`:
```csharp
var row = await LockItem(conn, tx, tenantId, itemId, ct);
if (row is null) { return new ReleaseResult.NotFound(); }
...
if (row.State == QueueStates.Open) { return new ReleaseResult.NotClaimed(); }
if (row.State != QueueStates.Claimed) { return new ReleaseResult.Closed(); }
if (!string.Equals(row.ClaimedBy, actor, StringComparison.Ordinal)) { return new ReleaseResult.NotClaimant(); }
var n = await conn.ExecuteAsync(new CommandDefinition(QueueSql.Release, new { tenant_id = tenantId, item_id = itemId, actor }, tx, cancellationToken: ct));
if (n != 1) { throw new InvalidOperationException("Release did not affect exactly one row."); }
await InsertItemEvent(conn, tx, tenantId, itemId, QueueStates.EventReleased, QueueStates.Claimed, QueueStates.Open, actor, null, ct);
return new ReleaseResult.Released(await Reload(conn, tx, tenantId, itemId, ct));
```
`DecideIdentityAsync` is `DecideAsync` (lines 233-299) minus the proposal CAS and `ProposalEvent`. It keeps the already-decided idempotency block (lines 249-255), the `claimed_by` guard (line 266) and the argument guards (lines 235-237). A defer branch reuses `QueueSql.Release` and emits event type `deferred`. In `row.State == Withdrawn` return `Withdrawn`, not `Closed`, for non-expiry withdrawals (line 264, and `IsWithdrawnExpired` stays distinct).

**Rationale never in event detail for decide** (comment lines 230-232). For defer, the D-09 and Pitfall 10 decision puts the rationale in the `deferred` event detail. Strip it in `QueueService.EventsAsync` for non-role holders.

**Detail JSON** (line 284) is built by string concatenation of static values. For a defer rationale, build it with `System.Text.Json` instead of concatenation, since the text is free-form.

**Create idempotency**: `INSERT ... ON CONFLICT DO NOTHING RETURNING item_id` with a follow-up SELECT (RESEARCH Pattern 4). Do not catch `23505`. Existing `InsertItem` is in `QueueSql.cs:7-9` and needs `ruleset_version` and `body_sha256` columns for the identity variant.

**Row mapping**: add `RulesetVersion` and `BodySha256` to `ItemRow` (lines 13-33), `ItemColumns` (`QueueSql.cs:15-16`), and `ToDetail` (lines 104-106). `Utc(DateTime)` helpers (lines 58-60) are used for every timestamp read.

### `src/Olive.Persistence/QueueSql.cs` (SQL constants)

**Analog:** itself. The conventions are public const strings, `tenant_id = @tenant_id` on every statement, `@now` supplied by the caller, and CAS `where state = ...` guards:
```csharp
public const string Release =
    "update queue_item set state = 'open', claimed_by = null, claimed_at = null " +
    "where tenant_id = @tenant_id and item_id = @item_id and state = 'claimed' and claimed_by = @actor";
public const string Withdraw =
    "update queue_item set state = 'withdrawn', withdrawn_reason = @reason " +
    "where tenant_id = @tenant_id and item_id = @item_id and state in ('open','claimed')";
```
Add `WithdrawByProducer` (also clears the claim and checks `producer`), `InsertIdentityItem`, `FindByProducerRef`, and `PollDecided` per RESEARCH "SQL for the new transitions". `List` (lines 29-33) already returns both kinds, so D-03 needs no SQL change. `Payload` (lines 39-42) uses an inner join on `proposal`, which is the reason for Pitfall 3.

### Razor pages `Pages/Review/Index`, `Item`, `Audit`

**Analog:** the same files (`src/Olive.Host/Pages/Review/*.cshtml(.cs)`, `ReviewerPageModel.cs`). They were not read in full here. The planner should read `Item.cshtml.cs` before planning the kind-driven form. RESEARCH "Razor pages" lists the exact changes: radios from `kind.Decisions`, per-kind rationale hint and finality note, a registry-driven stale-session validation, new `FlashMessages.Map` codes, handling `PayloadResult.NoPayload` in both Item and Audit, and the kind badge in Index reusing `.badge`. `UiSurfaceTests` must stay valid: no new page, no inline style, no `http` text.

### `contracts/reviewer/alpha-steward-1.json` etc.

**Analog:** `contracts/reviewer/alpha-sup-1.json`
```json
{
  "reviewer_id": "alpha-sup-1",
  "oid": "aaaaaaaa-0000-4000-8000-000000000001",
  "tid": "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
  "upn": "sup1.alpha@example.test",
  "groups": ["tenant-alpha-reviewers", "role:case_supervisor"]
}
```
Use `"role:identity_steward"` in `groups`, a fresh unique `oid` in the `aaaaaaaa-0000-4000-8000-0000000000NN` pattern, and the beta tenant-group name from `beta-sup.json`. Append the file names to `index.json` (currently a flat JSON array of 8 file names). `alpha-sup-1` already serves as the "supervisor without steward" fixture (RESEARCH TC-IRQ-04), so no extra fixture is needed. Update `README.md` and `ReviewerSessionTests` (`Assert.Equal(8, ...)` becomes 11).

### `contracts/service-token/*` and `contracts/introspection/index.json`

**Analog:** `contracts/introspection/` (`index.json` maps a visibly fake token string to a file; `application.json`, `inactive.json`, `introspection-response.schema.json`). Fixture shape for a bluto token is `{active, principal_type: "application", client_id, tenant_id, exp}` per D-05. `application.json` currently carries `key_id`, `purpose`, `data_class_ceiling` and `expires_at`, so do not copy those fields into the service-token schema.

Introspection index to extend with the service-token string as an `application` principal (IDN-03 evidence, RESEARCH Matrix note):
```json
"fixture-key-application": "application.json",
```
Add `contracts/service-token` to the `ContractSchemaTests.Folders` array.

### `contracts/queue-item/v1/*` (new create/withdraw/poll schemas)

**Analog:** `contracts/queue-item/v1/queue-item.schema.json`. It uses draft 2020-12, `additionalProperties: false`, the ref pattern `^(olive:proposal|bluto:candidate|bluto:review-evidence)/<uuid>$`, `ruleset_version` bounded by `minLength 1, maxLength 128`, and `"decision": enum [approve, reject, accept, defer]`. Every schema needs a paired `.valid.json` and `.invalid.json`, and the existing `queue-item.inline-payload.invalid.json` is the model for an inline-payload negative case.

### `tests/Olive.Tests/CredentialRouteMatrixTests.cs`

**Analog:** itself. Extend the enums and the table.
```csharp
public enum Cred { None, AgentKey, DevHeader, DevCookie, OidcCookie, ForgedCookie, ExpiredCookie }
public enum Route { Proposals, Catalog, QueueItems, Review, DevSignIn }
private static readonly string[] Paths = [...];
public static TheoryData<Cred, Route> Cells() { ... }
public static Expected Expect(Cred cred, Route route) => route switch ...
```
Add `Cred.ServiceToken` and `Route.QueueCreate/QueueWithdraw/QueuePoll`. `Route` needs a method and a body, because the `Build` helper currently always uses GET. Keep the `Expected` records (`NotPermitted`, `AgentUnauthenticated`, ...). The independent-expectation rule at line 109 applies: write the table from RESEARCH's admission matrix, not from handler behaviour. Also update `UI_07_cell_table_covers_seven_credentials_by_five_routes` (35 cells).

### `tests/Olive.Tests/IdentityReviewDemoTests.cs` and `build/demo-identity-review.sh`

**Analog:** `tests/Olive.Tests/Beat4DemoTests.cs` and `build/demo-beat4.sh`

Test class header (lines 9-16):
```csharp
[Trait("Category", "Integration")]
[Trait("Demo", "beat4")]
public sealed class Beat4DemoTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    ...
    await using var db = await fixture.CreateDatabaseAsync();
    await using var factory = new OliveApiFactory(db, configureServices: ...);
    using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
    var t = new StringBuilder("# Beat 4 transcript: ...\n\n");
```
Use `Demo=identity`, add `OliveApiFactory.CreateServiceClient(token)`, and write the transcript to `TestResults/identity-review-transcript.md` using numbered steps that quote only status codes and static codes. Beat4's `...runner_script_filters_to_the_trait` test is the model for the runner-script test.

Runner script, copy `build/demo-beat4.sh` verbatim with the trait and file names changed:
```bash
#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
dotnet test tests/Olive.Tests --filter "Demo=beat4"
transcript="$root/TestResults/beat4-transcript.md"
if [ ! -s "$transcript" ]; then echo "beat 4 transcript was not written: $transcript" >&2; exit 1; fi
echo "Transcript: $transcript"
```

### `sql/migrations/0010_identity_review_producer.sql`

**Analog:** `sql/migrations/0008_review_queue.sql` (queue tables, CHECKs, unique key, grants; not re-read, RESEARCH has quoted its relevant parts). The exact 0010 text is in RESEARCH "Migration 0010 (exact shape)". Constraints: forward-only, `^[0-9]{4}_[a-z0-9_]+\.sql$` file name, and 0001-0009 are checksum-pinned. Verify the event-type CHECK name in the migration test (Pitfall 11).

## Shared Patterns

### Fail-closed, no echoed values (problem+json)
**Source:** `ProblemResults.Result(status, ProblemReasons.X)` as used throughout `QueueEndpoints.cs`.
**Apply to:** every new handler and filter. Add each new code (`server_assigned_field`, `tenant_not_permitted`, `producer_not_registered`, `item_withdrawn`, `no_payload`) to `ProblemReasons` and to `ProblemResults.Title`. `ProblemReasonTitleTests` checks that every reason has a title.
```csharp
return ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ItemNotFound);
```

### Tenant isolation (two layers)
**Source:** `TenantUnitOfWork` (`uow.ExecuteAsync(tenantId, ...)`) plus `tenant_id = @tenant_id` in every statement in `QueueSql.cs`.
**Apply to:** every new SQL string and store method. For producer routes, also add `producer = @producer`.
The tenant comes from the verified token only (D-08 / D-05). It is never read from the body, query or header.

### Indistinguishable 404
**Source:** `ClaimResult.NotFound` / `ItemNotFound` mapping (`QueueEndpoints.cs:99`).
**Apply to:** withdraw for missing, cross-tenant and cross-producer items. Check `producer` and `kind` after `LockItem` and return `NotFound` on mismatch.

### Outcomes as result records, not exceptions
**Source:** `QueueService.cs` lines 121-176 and `PostgresQueueStore` (comment on line 8: results commit, so expiry persists before a 409).
**Apply to:** `CreateResult`, `WithdrawResult`, `PollResult` and the decide additions. Exceptions are only for invariant violations (`n != 1`).

### Fixture stubs barred in production
**Source:** `StubKeyIntrospector : IKeyIntrospector, INonProductionOnly` (FND-05, `StartupGuards.CheckNoNonProductionComponents`).
**Apply to:** `StubServiceTokenVerifier`. Extend `StartupGuardTests` and `ArchitectureTests` (new inbound token type only on Popeye-destination ports, port carries `[OutboundPort(OutboundDestination.Popeye)]`).

### Redacting secret types
**Source:** `src/Olive.Domain/SessionKey.cs`.
**Apply to:** `InboundBearerToken`. Extend the IDN-08 sentinel scans to cover the service token and the defer rationale.

### Static, strict config registries
**Source:** `AgentAllowlist.Parse` and `LoadEmbedded`.
**Apply to:** `ProducerRegistry`. Also add a non-override test mirroring `AgentAllowlistTests`.

### Time injection
**Source:** `QueueService` takes `TimeProvider time` and passes `time.GetUtcNow()` down. Stores take `DateTimeOffset now` and pass `now.UtcDateTime` to Npgsql. `AgentKeyAuthenticationHandler` uses `time.GetUtcNow()` for expiry.
**Apply to:** create and withdraw timestamps, and the defer and decide timestamps. See Pitfall 7 on taking `now` inside the transaction for `decided_at`.

### Test naming
Tests cite requirement IDs in the method name (`IRQ_01_...`, `EVD_03_...`). Traits are `Category=Integration|Unit|Architecture`, plus `Demo=...`.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `src/Olive.Application/ProducerCursor.cs` | utility | transform | No opaque pagination cursor exists. Use the codec spec in RESEARCH "Cursor codec" and `System.Buffers.Text.Base64Url`. |
| Policy scheme `QueueCaller` registration in `Program.cs` | config | request-response | No policy scheme exists yet. Use RESEARCH Pattern 1 (`AddPolicyScheme` with `ForwardDefaultSelector`). The registration goes into the existing `AddAuthentication(...)` chain, whose current form I did not read. |

## Notes for the Planner

- Pitfall 1 matters most for tests. `fixture-key-liam-alpha` and `fixture-key-user` must also be present in the service-token stub index, or the agent and user cells of the matrix will return 401 instead of 403.
- `JsonGate.TryAdmit` requires `run_id`. For create, use a plain strict parse plus `JsonGate.Walk(root)`, then `Jcs.Canonicalize` and `Jcs.Sha256Hex`.
- The `IQueueStore` fakes in tests need the new members (Pitfall 8). Decide up front between extending `IQueueStore` and adding a second interface.
- I did not read these files, so read them before drafting plan actions: `Program.cs`, `OliveClaims.cs`, `ReviewerClaims.cs`, `AgentKeyHeader`, `ProposalEndpoints.ReadBoundedAsync`, `StartupGuardFilter.cs`, `IQueueStore.cs`, and the Razor page models.

## Metadata

**Analog search scope:** `src/Olive.Host`, `src/Olive.Application`, `src/Olive.Persistence`, `src/Olive.Domain`, `src/Olive.Catalog`, `src/Olive.Integrations.Popeye`, `contracts/`, `config/`, `build/`, `tests/Olive.Tests`
**Files read in full or in part:** 14
**Pattern extraction date:** 2026-10-25
