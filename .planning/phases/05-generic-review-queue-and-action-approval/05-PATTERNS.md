# Phase 5: Generic Review Queue and Action Approval - Pattern Map

**Mapped:** 2026-10-23
**Files analyzed:** 38 new/modified (grouped below)
**Analogs found:** 36 / 38 (2 partial: ASP.NET two-scheme challenge override, N-parallel claim test)

Line numbers refer to the files as read on 2026-10-23. All paths are relative to `C:\Users\OMack\olive`.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match |
|---|---|---|---|---|
| `sql/migrations/0008_review_queue.sql` | migration | CRUD + RLS | `sql/migrations/0001_init.sql` (+0002/0003/0007 for grants) | exact |
| `src/Olive.Domain/QueueStates.cs` | constants | n/a | `src/Olive.Domain/ProposalStates.cs` | exact |
| `src/Olive.Application/IQueueKindRegistry.cs`, `IReviewerTenantMap.cs` | interface + records | request-response | `src/Olive.Application/IAgentAllowlist.cs` | exact |
| `src/Olive.Application/IUserIdentityResolver.cs` | port | request-response | `src/Olive.Application/IDirectoryGroupResolver.cs` | exact |
| `src/Olive.Application/UserIdentityVerifier.cs` | service | request-response (timeout/fail-closed) | `src/Olive.Application/DirectoryVerifier.cs` | exact |
| `src/Olive.Application/IQueueStore.cs`, `ReviewerIdentity.cs` | interface + records | CRUD | `src/Olive.Application/IProposalStore.cs` | exact |
| `src/Olive.Application/QueueService.cs` | service | request-response | `DirectoryVerifier.cs` (orchestration + result union); `TraceVerifier.cs` | role-match |
| `src/Olive.Application/IReviewerIdentitySource.cs` | port | request-response | `IDirectoryGroupResolver.cs` | role-match |
| `src/Olive.Catalog/QueueKindRegistry.cs`, `ReviewerTenantMap.cs` | config parser | transform | `src/Olive.Catalog/AgentAllowlist.cs` | exact |
| `config/queue-kinds.yaml`, `config/reviewer-tenants.yaml` | config | n/a | `config/agents.yaml` | exact |
| `src/Olive.Persistence/PostgresQueueStore.cs` | store | CRUD, tx, CAS | `src/Olive.Persistence/PostgresProposalStore.cs` | exact |
| `src/Olive.Persistence/QueueSql.cs` | SQL constants | CRUD | `src/Olive.Persistence/ProposalSql.cs` | exact |
| `src/Olive.Persistence/PostgresProposalStore.cs` (modify) | store | CRUD, tx | itself, `InsertEvaluatedAsync` accepted branch | exact |
| `src/Olive.Integrations.Entra/FixtureUserIdentityResolver.cs` | fixture port | request-response | `FixtureDirectoryGroupResolver.cs` | exact |
| `src/Olive.Integrations.Entra/FixtureReviewerIdentitySource.cs` | fixture port | request-response | `FixtureDirectoryGroupResolver.cs` | exact |
| `src/Olive.Integrations.Entra/Olive.Integrations.Entra.csproj` (modify) | build | n/a | itself (`EmbeddedResource` line) | exact |
| `contracts/reviewer/*`, user-identity fixtures | fixtures + schema | n/a | `contracts/directory/*` | exact |
| `contracts/queue-item/v1/*` (create), `v0/` (delete) | contract | n/a | `contracts/queue-item/v0/*`, `contracts/proposal/v1/*` | exact |
| `src/Olive.Host/Auth/DevReviewerAuthenticationHandler.cs` | auth handler | request-response | `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs` | exact |
| `src/Olive.Host/Auth/ReviewerClaims.cs` | claims | transform | `src/Olive.Host/Auth/OliveClaims.cs` | exact |
| `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs` (modify) | auth handler | request-response | itself | exact |
| `src/Olive.Host/Endpoints/QueueEndpoints.cs` | route | request-response | `src/Olive.Host/Endpoints/ProposalEndpoints.cs` | exact |
| `src/Olive.Host/Program.cs` (modify) | composition | n/a | itself | exact |
| `src/Olive.Host/Startup/StartupGuards.cs` (modify) | guard | n/a | `CheckCatalog`, `CheckNoNonProductionComponents` | exact |
| `src/Olive.Host/Problems/ProblemResults.cs` + `src/Olive.Contracts/ProblemReasons.cs` (modify) | problem+json | n/a | themselves | exact |
| `src/Olive.Contracts/QueueDtos.cs` | DTOs | request-response | `src/Olive.Contracts/ProposalDtos.cs` | exact |
| `tests/Olive.Tests/Queue*Tests.cs`, `ReviewerAuthTests.cs`, `SelfApprovalTests.cs` | test | HTTP integration | `tests/Olive.Tests/ExpiryTests.cs`, `PolicyEndpointTests.cs` | exact |
| `tests/Olive.Tests/Beat4DemoTests.cs`, `build/demo-beat4.sh` | test/script | HTTP integration | `ApiProposalTests.cs`; `build/vulnerable-gate.sh` | role-match |
| `PrivilegeMatrixTests`, `RlsPatternTests`, `MigrationRunnerTests`, `TenantIsolationAblationTests`, `TestDatabase`, `ContractSchemaTests`, `StartupGuardTests` (modify) | test | DB | themselves | exact |
| `tests/Olive.Tests/RecordingPorts.cs`, `FaultingDoubles.cs`, `OliveApiFactory.cs` (modify) | test double | n/a | themselves | exact |

## Pattern Assignments

### `sql/migrations/0008_review_queue.sql` (migration)

**Analog:** `sql/migrations/0001_init.sql`, `0002_proposal_column_grants.sql`, `0003_proposal_insert_columns.sql`, `0007_policy_expiry_rate.sql`. Use RESEARCH Pattern 1 for column and CHECK content.

**Header-comment style** (0007 lines 1-4): leading `--` comments naming requirement, grant rationale, and "0001-000N are checksum-pinned and unchanged".

**RLS block, copy verbatim per table** (0001 lines 33-43). `RlsPatternTests` asserts `current_setting('olive.tenant_id'::text)` with no `missing_ok`:
```sql
alter table proposal_event enable row level security;
alter table proposal_event force row level security;
create policy tenant_isolation on proposal_event
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);
```
**Composite FK + tenant-first PK** (0001 lines 22-31): `primary key (tenant_id, event_id)`, `foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id)`.

**Grant pattern** (0001 lines 45-50; 0003 line 5; 0002 line 4):
```sql
revoke all on proposal, proposal_event from public;
grant select, insert on proposal_event to olive_app;       -- queue_item_event: INSERT-only, no update/delete (Q9)
grant select on proposal, proposal_event to olive_reader;
grant insert (tenant_id, proposal_id, ...) on proposal to olive_app;   -- state excluded so it takes the default
grant update (state) on proposal to olive_app;                          -- column-level UPDATE
```
For `queue_item`: `grant select`, column `grant insert (...)` without `state`/`created_at`, column `grant update (state, claimed_by, ...)`. Do not `grant ... on schema`; usage was granted in 0001.

**Index pattern** (0007 line 7): `create index proposal_rate_idx on proposal (tenant_id, agent_id, action_type, created_at);`

---

### `src/Olive.Domain/QueueStates.cs` (constants)

**Analog:** `src/Olive.Domain/ProposalStates.cs`. Copy its constants + `CanTransition` style (read before writing). `ProposalStates.CanTransition(from,to)` already allows `awaiting_approval -> approved | rejected | expired`; `PostgresQueueStore` must call it before the SQL, as `TransitionAsync` does.

---

### `src/Olive.Application/IQueueKindRegistry.cs`, `IReviewerTenantMap.cs`, `IQueueStore.cs`

**Analog:** `src/Olive.Application/IAgentAllowlist.cs` (interface in Application, parser in Catalog; Application cannot reference Catalog per `ArchitectureTests`). `IQueueStore` is a new interface; do NOT add methods to `IProposalStore` (forces edits to every test double).

**Result-union style** (`IDirectoryGroupResolver.cs` lines 3-10), copy for `ClaimResult`, `ReleaseResult`, `DecideResult`:
```csharp
public abstract record DirectoryLookup
{
    public sealed record Found(IReadOnlyList<string> Groups) : DirectoryLookup;
    public sealed record UnknownUser : DirectoryLookup;
    public sealed record Unavailable : DirectoryLookup;
}
```
Rule: store methods return these unions for every business outcome and never throw inside `uow.ExecuteAsync` (a throw rolls back the lazy-expiry commit, POL-08).

---

### `src/Olive.Application/IUserIdentityResolver.cs` (port)

**Analog:** `src/Olive.Application/IDirectoryGroupResolver.cs` lines 12-14:
```csharp
// D-02: read-only directory lookup. No session key and no service token by construction. onBehalfOf is opaque and verbatim (D-06).
[OutboundPort(OutboundDestination.Directory)]
public interface IDirectoryGroupResolver { Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct); }
```
New port reuses `OutboundDestination.Directory`; lookup union: `Resolved(Oid,Tid) | NotFound | Ambiguous | Unavailable | Malformed`. Before planning, grep `OutboundPort` in `ContainmentTests.cs` / `ArchitectureTests.cs` for enumerations of ports.

---

### `src/Olive.Application/UserIdentityVerifier.cs` (service, fail-closed wrapper)

**Analog:** `src/Olive.Application/DirectoryVerifier.cs` (lines 15-58). Copy options record, linked-CTS timeout, catch-all to `Unavailable`:
```csharp
public sealed record DirectoryVerifierOptions(TimeSpan Timeout)
{
    public DirectoryVerifierOptions() : this(TimeSpan.FromSeconds(5)) { }
}
...
using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
{
    cts.CancelAfter(options.Timeout);
    try { lookup = await resolver.ResolveAsync(tenantId, onBehalfOf, cts.Token); }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    { return new DirectoryVerdict.Unavailable("directory_resolver_timeout"); }
    catch (Exception e) when (!ct.IsCancellationRequested)
    { return new DirectoryVerdict.Unavailable("directory_resolver_exception", e.GetType().Name); }
}
```
Cause strings are static; `ExceptionType` is a type name only, never message. `Program.cs` reads timeouts via `ReadTimeoutMs(configuration, "OLIVE_..._TIMEOUT_MS")` (lines 49-50); optional to add a new setting (a reuse of default is fine).

---

### `src/Olive.Application/QueueService.cs` (service)

**Analogs:** `DirectoryVerifier.cs` (orchestration with verdict unions); `ProposalEndpoints.PostAsync` for the "cheap and stateless first, port call outside any transaction, then store" ordering. Time via injected `TimeProvider` (`timeProvider.GetUtcNow()`, ProposalEndpoints line 256). Service order is RESEARCH Pattern 5 steps 1-8. Role gate reads `IQueueKindRegistry` `required_role`; never hardcode `role:case_supervisor`.

---

### `src/Olive.Catalog/QueueKindRegistry.cs`, `ReviewerTenantMap.cs` (strict YAML parser)

**Analog:** `src/Olive.Catalog/AgentAllowlist.cs`. Copy whole structure.

**Imports + strict deserialize + static message** (lines 1-37):
```csharp
private const string Invalid = "Agent allowlist is invalid.";
private sealed class AllowlistDocument { public List<AgentEntry>? Agents { get; set; } }
[GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$")] private static partial Regex IdPattern();
doc = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build()
    .Deserialize<AllowlistDocument?>(yaml);          // IgnoreUnmatchedProperties deliberately NOT set
catch (YamlDotNet.Core.YamlException) { throw new InvalidOperationException(Invalid); }
```
**Validation loop** (lines 44-62): null/pattern/duplicate -> `throw new InvalidOperationException(Invalid)`; build `Dictionary<..>(StringComparer.Ordinal)`.
**Embedded load** (lines 65-71):
```csharp
using var s = typeof(AgentAllowlist).Assembly.GetManifestResourceStream("config/agents.yaml")
    ?? throw new InvalidOperationException(Invalid);
using var r = new StreamReader(s);
return Parse(r.ReadToEnd());
```
`Olive.Catalog.csproj` already embeds `config\*.yaml` with `LogicalName="config/%(Filename)%(Extension)"`; no csproj edit needed for the two new YAML files. Message must be static (no content quoting). No `Register`/`Add` member on the registry (add reflection test like the catalog one).

**YAML analog:** `config/agents.yaml` (comment header "Source-controlled and change-managed like the catalog. Not overridable by environment variable or appsettings.", then a top-level list key).

---

### `src/Olive.Persistence/PostgresQueueStore.cs` + `QueueSql.cs` (store, CAS in one tx)

**Analog:** `src/Olive.Persistence/PostgresProposalStore.cs` and `ProposalSql.cs`.

**Class shape / Dapper mapping / Utc helper** (PostgresProposalStore lines 6-8, 34):
```csharp
public sealed class PostgresProposalStore(TenantUnitOfWork uow) : IProposalStore
{
    static PostgresProposalStore() => DefaultTypeMap.MatchNamesWithUnderscores = true;
    private sealed class Row { public Guid ProposalId { get; set; } ... public DateTime? ExpiresAt { get; set; } }
    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
```
**Tx entry** (lines 238-246): every method is `uow.ExecuteAsync<T>(tenantId, async (conn, tx) => { ... }, ct)` with `new CommandDefinition(Sql.X, new { tenant_id = tenantId, ... }, tx, cancellationToken: ct)`. `TenantUnitOfWork.ExecuteAsync` (lines 11-24) commits only when the lambda returns normally and calls `set_config('olive.tenant_id', @t, true)`.

**CAS + event pattern** (lines 227-235 and 282-303):
```csharp
var accepted = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.UpdateState,
    new { tenant_id = tenantId, proposal_id = id, from_state = ..., to_state = ... }, tx, cancellationToken: ct));
if (accepted != 1) { throw new InvalidOperationException("Accept compare-and-set did not affect exactly one row."); }
await Event("transition", "proposed", "awaiting_approval", null);
```
Local `Event(...)` helper (lines 199-209) wraps `ProposalSql.InsertEvent`; replicate for `QueueSql.InsertEvent` and reuse `ProposalSql.UpdateState` / `ProposalSql.InsertEvent` directly on the SAME `(conn, tx)` for decide and expiry. Do NOT call `IProposalStore.TransitionAsync` (lines 266-304): it opens its own transaction (breaks APR-04). Copy its pre-SQL guard: `if (!Olive.Domain.ProposalStates.CanTransition(fromState, toState)) throw new ArgumentException("POL-06: ...")`.

**Throw only for invariant violation** (n != 1 after lock): that is correct and rolls back. Business outcomes return unions.

**SQL constants style** (`ProposalSql.cs` lines 3-4, 36-37): header comment "Every statement keeps the application tenant predicate even though forced RLS also applies"; `public const string` with string concatenation; `update ... where tenant_id = @tenant_id and ... and state = @from_state`. `GetForUpdate` appends `for update`. Event jsonb param uses `@detail::jsonb` (line 13). Event ordering by `seq`, not `created_at`.

---

### `src/Olive.Persistence/PostgresProposalStore.cs` (MODIFY, QUE-03 enqueue)

Insert point: immediately after `await Event("transition", "proposed", "awaiting_approval", null);` at line 234, before `return new EvaluatedInsertResult(id, ..., AwaitingApproval, null)` (line 235), inside the accepted branch only. `p.ExpiresAt` is guaranteed non-null by the guard at lines 156-159. Do NOT add to `InsertCoreAsync` / `InsertRefusedAsync` (refusal paths must never enqueue, which structurally satisfies EVD-03). Code shape is in RESEARCH Pattern 3 (`QueueSql.InsertItem`, `QueueSql.InsertEvent`, actor `p.AgentId`, same `tx`). Idempotent replay returns at lines 194-197 before this point.

---

### `src/Olive.Integrations.Entra/FixtureUserIdentityResolver.cs`, `FixtureReviewerIdentitySource.cs`

**Analog:** `src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs` (whole file). Copy:
```csharp
public sealed class FixtureDirectoryGroupResolver : IDirectoryGroupResolver, INonProductionOnly
{
    private readonly ConcurrentDictionary<(Guid Tenant, string User), string[]> _groups = new();
    public static FixtureDirectoryGroupResolver CreateDefault() { ... reads "directory/index.json" then each "directory/" + file via GetManifestResourceStream ... }
    public void Register(Guid tenantId, string onBehalfOf, IEnumerable<string> groups) => _groups[(tenantId, onBehalfOf)] = [.. groups];
    private static string ReadResource(Assembly asm, string name) { ... ?? throw new InvalidOperationException("A directory fixture resource is missing."); }
```
Adaptation: value type becomes `List<(string Oid, string Tid)>` with `Register` appending so `Count > 1` yields `Ambiguous`. Both classes implement `INonProductionOnly` (that is what the production guard sees). Keep `OrdinalIgnoreCase` for oid/tid comparison happening in the service, not the fixture.

**csproj edit** (`Olive.Integrations.Entra.csproj` line 7): add one `EmbeddedResource` per new fixture folder following
`<EmbeddedResource Include="..\..\contracts\directory\*.json" LogicalName="directory/%(Filename)%(Extension)" />`.

---

### `contracts/reviewer/*`, user-identity fixtures

**Analog:** `contracts/directory/` (README.md, `directory-groups.schema.json`, `index.json` as a JSON string array, one JSON per fixture). Schema skeleton to copy: `$schema` 2020-12, `$id https://olive.invalid/contracts/...`, `additionalProperties:false`, `required`, GUID pattern `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`. Fixture example: `analyst-alpha.json` with tenant `5325ffe9-5727-4d88-9832-aebabb8d0538` and `on_behalf_of: analyst.alpha@example.test`. Each fixture folder also needs a schema with valid/invalid examples. Note `index.json` has no `.schema.json` suffix, so `ContractSchemaTests` folder scan only globs `*.schema.json`.

---

### `contracts/queue-item/v1/*` (replace v0)

**Analog:** `contracts/queue-item/v0/queue-item.schema.json` (single-line JSON, `additionalProperties:false`, `required` list, `format: uuid`) and `contracts/proposal/v1/` (README + `.schema.json` + `.valid.json` + `.invalid.json` + extra named variants like `proposal-submit-response.badreason.invalid.json`). Add `queue-item.inline-payload.invalid.json` in that naming style. Housekeeping:
- `git rm -r contracts/queue-item/v0`.
- `tests/Olive.Tests/ContractSchemaTests.cs` line 10: change `"contracts/queue-item/v0"` to `"contracts/queue-item/v1"` in `Folders`. Test API_05 requires `.valid.json` and `.invalid.json` beside each schema.
- `CLAUDE.md` mentions `queue-item/v0/` (approx lines 66, 131); update.
- If the schema is to be embedded for a response-shape test, mirror `Olive.Catalog.csproj` `contracts\proposal\v1\*.schema.json` embedding; otherwise load from `TestPaths.Root` like `ExpiryTests` does for introspection fixtures.

---

### `src/Olive.Host/Auth/DevReviewerAuthenticationHandler.cs`

**Analog:** `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs` (lines 11-44). Copy ctor shape and members:
```csharp
public sealed class AgentKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IKeyIntrospector introspector, TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentKey";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync() { ... return AuthenticateResult.Fail(ProblemReasons.KeyInactive); ... AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)); }
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = AgentKeyHeader.Scheme;
        await ProblemResults.WriteAsync(Context, StatusCodes.Status401Unauthorized, ProblemReasons.KeyInactive);
    }
}
```
Differences: read a dedicated header (not `Authorization`); depend on `IReviewerIdentitySource` (INonProductionOnly impl) and `IReviewerTenantMap`; override `HandleChallengeAsync` to write 403 `principal_not_permitted` when an agent `Authorization: Bearer` header is present with no reviewer header, else 401 `reviewer_unauthenticated`. Also override `HandleForbiddenAsync` only if needed for tenant-unresolved 403. `ProblemResults.WriteAsync(context, status, reason)` is the existing helper for handler-level problems.
Mirror modification on `AgentKeyAuthenticationHandler`: `NoResult` for paths starting `/v1/queue` (no introspection call), and challenge returns 403 `principal_not_permitted` when the reviewer header is present and no agent key. Keep header-less path 401 `key_inactive` (StartupGuardTests probes rely on it). Partial analog: no existing two-scheme code; RESEARCH recommends a Wave-0 spike test of the 4-case caller matrix.

### `src/Olive.Host/Auth/ReviewerClaims.cs`

**Analog:** `src/Olive.Host/Auth/OliveClaims.cs`: `const string` claim names `olive:...`, a static `ToPrincipal(..., string scheme)` building `new ClaimsIdentity(claims, scheme)`, and a `ToX(this ClaimsPrincipal user)` extension using `user.FindFirstValue(...)`. Add `olive:reviewer_oid`, `olive:reviewer_tid`, repeated `olive:group`, `olive:auth_source`, `olive:tenant_id` (only when exactly one tenant maps). Reuse `OliveClaims.TenantId` constant name for consistency.

---

### `src/Olive.Host/Endpoints/QueueEndpoints.cs`

**Analog:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs`.

**Registration** (lines 19-24):
```csharp
public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
{
    app.MapPost("/v1/proposals", PostAsync).RequireAuthorization();
    app.MapGet("/v1/proposals/{id:guid}", GetAsync).RequireAuthorization();
    return app;
}
```
New: `MapQueueEndpoints` using `app.MapGroup("/v1/queue/items")` with `RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DevReviewerAuthenticationHandler.SchemeName })`; `{id:guid}` route constraint; call `app.MapQueueEndpoints()` after line 65 of `Program.cs`.

**Principal check then 404-as-not-found** (lines 31-35, 353-360): 
```csharp
var key = http.User.ToActiveKey();
if (key.PrincipalType != PrincipalTypes.Agent) { return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted); }
...
var r = await store.GetAsync(key.TenantId, id, ct);
if (r is null) { return ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ProposalNotFound); }
```
**Body hygiene** (lines 42-60): `HasJsonContentType()`, bounded read via `ReadBoundedAsync`, `JsonGate.TryAdmit(...)`, `Invalid()` helper returning 400 `invalid_request`. Reuse for the decision body; reject unknown fields such as `decided_by` or `tenant_id` as `invalid_request`.
**Exhaustive switch for unions:** map `ClaimResult`/`DecideResult` with a switch expression, no `_` arm.
**503 retryable** (line 316-318): `ProblemResults.Retryable(StatusCodes.Status503ServiceUnavailable, ProblemReasons.X)` for `identity_resolution_unavailable`.
**Logging** (lines 87-88, 116): `logger.LogWarning/LogTrace` with ids and static codes only, never rationale, parameters, UPN.
**Tenant:** read only from the reviewer claim, never request values (mirror "Tenant comes only from the introspected key", line 221).

---

### `src/Olive.Host/Problems/ProblemResults.cs`, `src/Olive.Contracts/ProblemReasons.cs` (MODIFY)

`ProblemReasons.cs` lines 6-22: add `public const string` per new reason (`already_claimed`, `not_claimant`, `decision_conflict`, `proposal_expired`, `item_not_claimed`, `item_closed`, `role_not_permitted`, `self_approval_forbidden`, `approval_identity_unresolved`, `identity_resolution_unavailable`, `reviewer_unauthenticated`, `reviewer_tenant_unresolved`, `item_not_found`, `kind_not_supported`).
`ProblemResults.Title` (lines 21-38) is a switch with fallback `_ => "An internal error occurred."`; EVERY new reason needs a case or it silently renders the fallback. Add the table-driven reflection test over all constants. Titles are static text only.

---

### `src/Olive.Host/Startup/StartupGuards.cs` (MODIFY) and `Program.cs` (MODIFY)

`StartupGuards.CheckCatalog` (lines 29-43) is the template for `CheckQueueKinds` (resolve the registry + run D-21 equality of catalog `required_approval` vs kind `required_role`; catch-all -> `throw new StartupGuardException("API-10"-style id, "static message")`). Wire it where `CheckCatalog` is invoked (read `StartupGuardFilter.cs` first). `CheckNoNonProductionComponents` (lines 45-98) scans descriptors and resolved instances for `INonProductionOnly`; because a scheme handler is not a service, the fixture sources register as singletons and trip FND-05 automatically. Add an explicit production check on `IAuthenticationSchemeProvider.GetSchemeAsync("DevReviewer")`.

`Program.cs` registration templates:
- line 51-52: `builder.Services.AddSingleton<IDirectoryGroupResolver>(_ => FixtureDirectoryGroupResolver.CreateDefault()); builder.Services.AddSingleton<DirectoryVerifier>();`
- line 53: `builder.Services.AddSingleton<IAgentAllowlist>(_ => AgentAllowlist.LoadEmbedded());`
- lines 55-57: `AddAuthentication(AgentKeyAuthenticationHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(SchemeName, _ => { })`; chain a second `.AddScheme<..., DevReviewerAuthenticationHandler>(...)`; keep AgentKey as default.
- line 35: `builder.Services.AddSingleton<IProposalStore, PostgresProposalStore>();` -> add `IQueueStore, PostgresQueueStore`.

---

### `src/Olive.Contracts/QueueDtos.cs`

**Analog:** `src/Olive.Contracts/ProposalDtos.cs` (read before writing; records with snake_case JSON). Use IR-1 field names only on the list DTO (D-14).

---

### Test files (new)

**Analog:** `tests/Olive.Tests/ExpiryTests.cs`.
- Class attrs and ctor: `[Trait("Category", "Integration")] public sealed class ExpiryTests(PostgresFixture fixture)`; `private static CancellationToken Ct => TestContext.Current.CancellationToken;`.
- DB per test: `await using var db = await fixture.CreateDatabaseAsync();`
- Clock: `var clock = new ManualTimeProvider(NewStart()); new OliveApiFactory(db, introspector, configureServices: s => s.AddSingleton<TimeProvider>(clock));` (ExpiryTests lines 22-27; `ManualTimeProvider` has `Set`/`Advance`).
- Client: `factory.CreateKeyClient(ApiProposalTests.AlphaKey)`; add `CreateReviewerClient(id)` to `OliveApiFactory` (lines 112-120 are the model).
- Submit: `c.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct)` with `ApiProposalTests.BodyFor(run)`.
- Name tests with requirement IDs (`QUE_05_...`, `POL_08_...`).
- Replacing a port: `OliveApiFactory` takes inner ports (`innerDirectory:` param, line 22); add a `IUserIdentityResolver` equivalent plus `RecordingUserIdentityResolver` in `RecordingPorts.cs` and a faulting double in `FaultingDoubles.cs` (test project only). The `AdvancingDirectory` decorator (ExpiryTests lines 32-40) is the template for "clock advances mid-request".
- Parallel claim (partial analog): none exists; use a pool with `MaxPoolSize >= N` (not `Pool1`), distinct reviewer fixtures, assert 1 winner, N-1 `already_claimed`, one `claimed` event row.
- Verifying committed expiry: read through `olive_reader` on a separate connection as `ApiProposalTests.ReaderScalarAsync` does.

**Modified existing tests (explicit edits required, they do not auto-extend):**
- `PrivilegeMatrixTests.cs` lines 12-20 `Expected` array: append `("queue_item","olive_app",["SELECT"])`, `("queue_item","olive_reader",["SELECT"])`, `("queue_item_event","olive_app",["SELECT","INSERT"])`, `("queue_item_event","olive_reader",["SELECT"])`. Column-level UPDATE/INSERT make `has_table_privilege` false for olive_app. Add column tests mirroring `DB_04_app_update_is_limited_to_state_and_refusal_reason_columns` (line 62) using the `SqlStateAsync` helper (lines 49-60).
- `RlsPatternTests.ExpectedTables`; `MigrationRunnerTests.DB_02_applies_0001_...` hard-coded `["0001".."0007"]` list plus the `expected8` checksum assertion; `TenantIsolationAblationTests` + `TestDatabase.SeedQueueItemAsync` + `AblationDatabase` table names; `StartupGuardTests` (FND_05 production-with-stub cases for the two new fixtures); `ContractSchemaTests.Folders`.

### `tests/Olive.Tests/Beat4DemoTests.cs`, `build/demo-beat4.sh`

Test: `ApiProposalTests` helpers + `SentinelScan` for transcript hygiene; transcript path `Path.Combine(TestPaths.Root, "TestResults", "beat4-transcript.md")` (`TestResults/` is gitignored). Attrs: `[Trait("Demo","beat4")]` and `[Trait("Category","Integration")]`. Script analog: `build/vulnerable-gate.sh` (read its header/shebang and `set -euo pipefail` style before writing); body `dotnet test tests/Olive.Tests --filter "Demo=beat4"`. Check `.github/workflows/ci.yml` (uses `--filter "Category!=Integration"` for quick runs); demo runs in the full suite automatically.

## Shared Patterns

### Tenant isolation, two layers
**Source:** `ProposalSql.cs` header comment + `TenantUnitOfWork.cs` lines 9-20. **Apply to:** every `QueueSql` statement and every store method: `where tenant_id = @tenant_id` AND run inside `uow.ExecuteAsync(tenantId, ...)`. Cross-tenant item = 404 indistinguishable from missing.

### Never throw for business outcomes inside a unit of work
**Source:** `TenantUnitOfWork.ExecuteAsync` line 21-22 (commit only on normal return). **Apply to:** `PostgresQueueStore` claim/release/decide/expiry. Lazy expiry must commit before `409 proposal_expired` returns. Invariant violations (`n != 1`) still throw `InvalidOperationException` as in `PostgresProposalStore` lines 229-232.

### Static problem+json, no echo
**Source:** `ProblemResults.cs` lines 20-38, 56-57. **Apply to:** all endpoints. No rationale, parameters, UPNs, or exception text in titles, logs, event detail, or transcript. Logs use ids + static reason codes (ProposalEndpoints lines 87-88, 202).

### Fail-closed port wrapper
**Source:** `DirectoryVerifier.cs`. **Apply to:** `IUserIdentityResolver` use on approve only (D-18); reject skips resolver.

### Non-production fixtures
**Source:** `FixtureDirectoryGroupResolver : IDirectoryGroupResolver, INonProductionOnly`; `StartupGuards.CheckNoNonProductionComponents`. **Apply to:** `FixtureUserIdentityResolver`, `FixtureReviewerIdentitySource`.

### Time injection
**Source:** `ProposalEndpoints` line 256 `timeProvider.GetUtcNow()`; auth handler line 30 `result.Key.ExpiresAt <= time.GetUtcNow()`. **Apply to:** expiry boundary `expires_at <= now` = expired (A2), passed as a parameter into store SQL (`@now`), never `now()` in the list/claim predicates.

### Requirement-ID naming
**Source:** CLAUDE.md "Requirements Traceability" + existing exception messages (`"POL-06: ..."`, `"POL-07: ..."`). **Apply to:** test names and guard messages.

## No Analog Found

| File / concern | Role | Data Flow | Reason |
|---|---|---|---|
| Two-scheme auth challenge overrides (403 for cross-caller) | auth | request-response | Only one scheme exists today; follow RESEARCH Pattern 8 and spike the 4-case matrix first |
| N-parallel claim race test | test | concurrency | No existing parallel-HTTP test; follow RESEARCH Pitfall 10 |
| `queue_item_event` `seq bigint generated always as identity` ordering | migration | audit | `proposal_event` orders only by `created_at`; use RESEARCH Pattern 1/6 |
| `SELECT ... FOR UPDATE` classify-then-CAS | store | CRUD | No existing row-lock reads (only advisory lock `ProposalSql.LockRate` line 22); verified in RESEARCH against postgres:17 |

## Metadata

**Analog search scope:** `src/**`, `sql/**`, `config/`, `contracts/directory`, `contracts/queue-item`, `contracts/proposal`, `tests/Olive.Tests/**`, `build/`
**Files read in full:** PostgresProposalStore, ProposalSql, TenantUnitOfWork, AgentKeyAuthenticationHandler, OliveClaims, AgentAllowlist, ProposalEndpoints, ProblemResults, ProblemReasons, FixtureDirectoryGroupResolver, IDirectoryGroupResolver, DirectoryVerifier, Program.cs, StartupGuards (lines 25-100), migrations 0001/0002/0003/0007, directory fixtures, queue-item v0 schema, ExpiryTests (top), PrivilegeMatrixTests (top)
**Pattern extraction date:** 2026-10-23
