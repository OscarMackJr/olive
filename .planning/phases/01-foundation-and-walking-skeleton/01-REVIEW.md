---
phase: 01-foundation-and-walking-skeleton
reviewed: 2026-10-21T23:32:28Z
depth: standard
files_reviewed: 59
files_reviewed_list:
  - .gitattributes
  - .github/workflows/ci.yml
  - build/vulnerable-gate.sh
  - BannedSymbols.txt
  - Directory.Build.props
  - Directory.Build.targets
  - Directory.Packages.props
  - config/agents.yaml
  - sql/bootstrap/001_roles.sql
  - sql/bootstrap/002_database.sql
  - sql/migrations/0001_init.sql
  - sql/migrations/0002_proposal_column_grants.sql
  - sql/migrations/0003_proposal_insert_columns.sql
  - src/Olive.Application/AgentKeyHeader.cs
  - src/Olive.Application/IAgentAllowlist.cs
  - src/Olive.Application/IKeyIntrospector.cs
  - src/Olive.Application/INonProductionOnly.cs
  - src/Olive.Application/IProposalStore.cs
  - src/Olive.Application/IntrospectionResult.cs
  - src/Olive.Application/IntrospectionUnavailableException.cs
  - src/Olive.Application/VendoredContracts.cs
  - src/Olive.Catalog/AgentAllowlist.cs
  - src/Olive.Contracts/ProblemReasons.cs
  - src/Olive.Contracts/ProposalDtos.cs
  - src/Olive.Domain/PrincipalTypes.cs
  - src/Olive.Domain/ProposalStates.cs
  - src/Olive.Domain/RedactingJsonConverter.cs
  - src/Olive.Domain/ServiceToken.cs
  - src/Olive.Domain/SessionKey.cs
  - src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs
  - src/Olive.Host/Auth/OliveClaims.cs
  - src/Olive.Host/Endpoints/ProposalEndpoints.cs
  - src/Olive.Host/Problems/ProblemExceptionHandler.cs
  - src/Olive.Host/Problems/ProblemResults.cs
  - src/Olive.Host/Program.cs
  - src/Olive.Host/Startup/DotEnvConfiguration.cs
  - src/Olive.Host/Startup/DotEnvFile.cs
  - src/Olive.Host/Startup/OliveEnvironment.cs
  - src/Olive.Host/Startup/ServiceCollectionAccessor.cs
  - src/Olive.Host/Startup/StartupGuardException.cs
  - src/Olive.Host/Startup/StartupGuardFilter.cs
  - src/Olive.Host/Startup/StartupGuards.cs
  - src/Olive.Integrations.Popeye/StubKeyIntrospector.cs
  - src/Olive.Migrator/Program.cs
  - src/Olive.Persistence/DatabaseSafetyChecks.cs
  - src/Olive.Persistence/MigrationCatalog.cs
  - src/Olive.Persistence/MigrationFile.cs
  - src/Olive.Persistence/MigrationRunner.cs
  - src/Olive.Persistence/MigratorCommand.cs
  - src/Olive.Persistence/PostgresProposalStore.cs
  - src/Olive.Persistence/PostgresUrl.cs
  - src/Olive.Persistence/ProposalSql.cs
  - src/Olive.Persistence/TenantUnitOfWork.cs
  - tests/Olive.Tests/ApiProposalTests.cs
  - tests/Olive.Tests/CiAndDevConfigTests.cs
  - tests/Olive.Tests/InMemoryLogCapture.cs
  - tests/Olive.Tests/MigrationRunnerTests.cs
  - tests/Olive.Tests/PrivilegeMatrixTests.cs
  - tests/Olive.Tests/VulnerableGateScriptTests.cs
findings:
  critical: 0
  warning: 7
  info: 13
  total: 20
status: issues_found
---

# Phase 1: Code Review Report

**Reviewed:** 2026-10-21T23:32:28Z (second re-review, after gap closure 01-11, 01-12, 01-13; first re-review 2026-10-21T22:57:54Z after 01-08..01-10; first pass 2026-10-21T00:00:00Z)
**Depth:** standard
**Files Reviewed:** 59
**Status:** issues_found

## Narrative Findings (AI reviewer)

## Re-review (gap closure wave 2)

Scope: `git diff 7223f94..HEAD` on `ProposalEndpoints.cs`, `ProposalSql.cs`, migration 0003, `build/vulnerable-gate.sh`, the CI workflow and the five test files (`ApiProposalTests`, `MigrationRunnerTests`, `PrivilegeMatrixTests`, `CiAndDevConfigTests`, `VulnerableGateScriptTests`). Findings outside this scope (WR-01, WR-03, WR-04, WR-06, WR-07, IN-01..IN-10) are carried forward unchanged.

| ID | Status | Fixed by |
|----|--------|----------|
| CR-02 | **Resolved** (an over-rejection side effect is now WR-10; a logging gap in the backstop is WR-11) | plan 01-11, `c9bdca5` (tests `6fe5ad6`) |
| WR-08 | **Resolved** (test-discrimination and scope residuals in IN-13) | plan 01-13, `42e142b` (tests `27df6c0`) |
| WR-09 | **Resolved** (`proposal_event` residual in IN-12) | plan 01-12, `2e3f553` (tests `e3c6b74`) |
| IN-11 | **Resolved** (escaped-pair positive case still missing; see IN-14) | plan 01-11, `6fe5ad6` |
| WR-10 | **New, open** | the numeric expansion budget charges digits that cannot expand, so valid bodies get 400 |
| WR-11 | **New, open** | the class-22 backstop swallows server-side data faults without logging anything |
| IN-12 | **New, open** | `olive_app` can still forge `proposal_event.created_at` and arbitrary events |
| IN-13 | **New, open** | gate tests do not tell failure reasons apart; the expected count comes from the same solution |
| IN-14 | **New, open** | the "escaped valid pair" positive case uses a literal emoji, not a JSON escape |

How I verified this:
- **CR-02:** I traced `IsNumberInRange` for every JSON number shape. With raw length <= 64 and |exponent| <= 308, the largest value is about 10^372 and the smallest about 10^-372. Both are far inside PostgreSQL numeric's range (131072 digits before the point, 16383 after), so no number that passes can raise 22003. `int.TryParse` with `AllowLeadingSign` handles `e+308`, `E-0` and leading-zero exponents (`1e000308`), and seven or more exponent digits are rejected before parsing. Worst-case `parameters::text` expansion is the 65,536 budget plus a few characters of `0.` and sign overhead per number, which bounds the GET amplification. The host builds its data source with `NpgsqlDataSource.Create` and no logger factory (`Program.cs:28`), so Npgsql itself does not log the backstop path at Error. A failed insert disposes `TenantUnitOfWork`'s transaction uncommitted, so it rolls back and leaves no partial row.
- **WR-09:**
  - 0003 is LF with no BOM (`git ls-files --eol`: `i/lf w/lf`). Its grant list matches every column in 0001 except `state` and `created_at`, which have defaults (`'proposed'`, `now()`).
  - `ProposalSql.Insert` no longer names `state`.
  - `on conflict ... do nothing returning proposal_id` needs only INSERT on the listed columns plus SELECT on `proposal_id`, and table-level SELECT is still granted.
  - `DB_04_app_cannot_insert_non_default_state` was RED before the fix, but not with 42501: the pre-0003 insert ran without a tenant GUC, so it would have failed in the RLS check with 42704. It is green now only because the privilege check runs before RLS. `DB_04_app_insert_is_limited_to_application_columns` walks the live `pg_attribute` list, so a column added later without a grant fails it loudly.
  - `SeedProposalAsync` inserts as `olive_app`, so the "can insert" test does exercise the column grant.
- **WR-08:** I ran the real pipeline on this repo (SDK 10.0.204). `dotnet sln Olive.sln list | grep -c '\.csproj$'` returns 12, `dotnet list package --vulnerable --include-transitive` exits 0 with 12 "has no vulnerable packages" lines, and `build/vulnerable-gate.sh` exits 0. With `set +e`, `${PIPESTATUS[0]}` gives `dotnet list`'s own status, not `tee`'s. If `dotnet sln list` fails, `grep -c` prints `0` and the script refuses the count as non-positive (fails closed). A CRLF-terminated report still matches, because grep matches a substring.

## Re-review (gap closure wave 1)

Scope: `git diff 013978f..HEAD` on the endpoint, migration 0002, the CI workflow and the six test files. Findings outside this scope (WR-01, WR-03, WR-04, WR-06, WR-07, IN-01..IN-10) are carried forward unchanged.

| ID | Status | Fixed by |
|----|--------|----------|
| CR-01 | **Resolved** | plan 01-08, `5fde023` (tests `91ceca1`, `901a804`) |
| WR-02 | **Resolved** (an INSERT-side residual is now WR-09) | plan 01-09, `13fde20` (tests `99fc28d`) |
| WR-05 | **Resolved** (the new count check is tautological; see WR-08) | plan 01-10, `ffe54fd` (tests `3fb9ea9`) |
| CR-02 | **New, open** | another client-triggerable 500 path, through `parameters` numbers |
| WR-08 | **New, open** | CI count check cannot fire |
| WR-09 | **New, open** | `olive_app` INSERT can forge `state` and `created_at` |
| IN-11 | **New, open** | CR-01 test coverage gaps |

How I verified this, using scratch probes (since deleted) on .NET 10.0.11 and `postgres:17-alpine`:
- `JsonElement.GetString()` and `JsonProperty.Name` throw `InvalidOperationException` for `\ud800`, `\udc00` and `a\ud800b`.
- Raw invalid UTF-8 bytes (`ED A0 80`, `FF`) pass `JsonDocument.Parse`, and then `GetString` and `Name` throw `InvalidOperationException`. The new catch blocks therefore cover this case too.
- An escaped valid pair (`\ud83d\ude00`) decodes to `d83d,de00` and is accepted by jsonb.
- `\u0000` decodes to U+0000, which `IsWellFormed` rejects.
- PostgreSQL rejects `\ud800` with 22P02 and `\u0000` with 22P05.
- PostgreSQL also rejects `{"x":1e131072}`, `1e-16384` and `-1e999999` with **22003 "value overflows numeric format"**. `System.Text.Json` accepts all three. This is CR-02.
- On this repo, `dotnet list package --vulnerable --include-transitive` prints one "The given project `X` has no vulnerable packages given the current sources." line per project. That is 12 lines, and every one of them matches both count greps (WR-08).

**CR-01 regression power (01-08 never confirmed RED):** I traced each new `LoneSurrogateBodies` case against the pre-fix endpoint (`013978f`):
- Cases 1–2 (`run_id`, `justification`): `GetString` threw with no catch, so the response was 500. The new test would have failed on the old code (RED).
- Cases 3–5 (`parameters` value, key, and key inside an array): the old `\\u0000` substring screen did not match, so `GetRawText` reached `::jsonb`. PostgreSQL returned 22P02, and the response was 500 (RED).
- Case 6 (`\u0000` key): the old substring screen already returned 400, so this case passes on both old and new code and cannot detect a regression.

The new `Assert.False(factory.Logs.HasErrorOrAbove)` also catches a regression that maps the fault to 400 but still logs at Error level. Overall the tests would catch a regression of the walk, the property-name check or the try/catch. The remaining gaps are listed in IN-11.

## Summary

I reviewed the Phase 1 walking skeleton: the host pipeline, agent-key authentication, the proposal endpoints, the startup guards, the persistence layer (tenant unit of work, Dapper store, migration runner and DB-07 checks), the bootstrap and migration SQL, and CI.

The core security spine holds up under tracing:
- Tenant comes only from the introspected key.
- Every statement carries an application tenant predicate.
- The GUC is transaction-local, and RLS policies use `current_setting` without `missing_ok`, so an unset tenant fails closed.
- `IntrospectionUnavailableException` reaches the exception handler, which returns 503.
- Secret types redact through `ToString`, `IFormattable`, `DebuggerDisplay` and JSON.
- ProblemDetails text is static.
- All SQL is parameterized. The only concatenation is the constant advisory-lock key.
- Migration checksums are taken over LF-pinned embedded bytes.

Problems found:
- **One client-triggerable contract violation (CR-01, now resolved).** Invalid UTF-16 in the body produced a 500 instead of a 400. A second path of the same class, out-of-range numbers in `parameters` (CR-02), is now resolved too. Its fix rejects some valid bodies (WR-10) and makes server-side data faults invisible (WR-11).
- **Gaps in the controls that are meant to be backstops:**
  - The FND-05 guard can be bypassed through decorators, factory registrations and keyed registrations.
  - The REQ-ACT-04 banned-API list is incomplete.
  - The CI vulnerable-package gate failed open (WR-05, resolved), and then its count check was tautological (WR-08, resolved).
  - The proposal row was fully UPDATE-able by `olive_app` (WR-02, resolved), and INSERT could set any `state` (WR-09, resolved). `proposal_event` still accepts a forged `created_at` (IN-12).
- **Unrestricted GET.** `GET /v1/proposals/{id}` is open to any active principal in the tenant, including application (service) credentials.

I verified the JSON behaviours with a scratch .NET 10 probe:
- Duplicate keys: the last value wins.
- A lone `\ud800` escape makes `GetString()` throw `InvalidOperationException`.
- `GetRawText()` passes the lone surrogate through unchanged.
- `UTF8Encoding(false).GetString` keeps a leading BOM.

## Critical Issues

### CR-01: Invalid Unicode in the request body returns 500 internal_error instead of 400, and logs an ERROR — RESOLVED

**Resolution (re-review 2026-10-21T22:57:54Z):** Fixed in plan 01-08, commit `5fde023`.
- **Fields:** `TryRunId` and `TryOptionalString` now go through `TryGetCleanString`, which catches `InvalidOperationException` and runs `IsWellFormed` (NUL and unpaired-surrogate scan).
- **`parameters`:** `TryParameters` walks the whole subtree with `AllStringsValid`. The walk covers object property names (`TryGetPropertyName` catches the throw from `JsonProperty.Name`), nested objects, arrays (including arrays of objects and arrays of strings) and string values. Numbers, booleans and null are not strings and cannot carry escapes.
- **Body depth:** `MaxDepth = 64` bounds the recursion.
- **Unvalidated root fields:** root-level property names and unknown root fields are never decoded or stored. `TryGetProperty("run_id")` still succeeds when a sibling key is a lone surrogate (probed), so they cannot cause a 500.
- **Other string paths:** every string that reaches SQL is now validated. These are `run_id`, `action_type`, `party_ref`, `justification`, `trace_ref`, `nono_session_id` and `parameters`. The other inserted values come from the introspected key.
- **Tests:** `API_01_lone_surrogate_and_nul_escapes_give_400_and_store_nothing` covers five of the six original paths. Coverage gaps are in IN-11.
- **Not covered:** the same class of failure through numbers is still open as CR-02.

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:127,150,167-174` (with `src/Olive.Host/Problems/ProblemExceptionHandler.cs:12-15`)

**Issue:** `JsonDocument.Parse` accepts a lone surrogate escape such as `"\ud800"`.

- **String fields:** `JsonElement.GetString()` then throws `InvalidOperationException` ("Cannot read incomplete UTF-16 JSON text as string with missing low surrogate"). This covers `run_id` (line 127) and every optional string field (line 150). The exception is not caught, so `ProblemExceptionHandler` maps it to `500 internal_error` and logs it at Error level.
- **`parameters`:** `GetRawText()` keeps the escape verbatim. Only `\u0000` is screened, so `{"parameters":{"x":"\ud800"}}` reaches `@parameters::jsonb`. PostgreSQL rejects it (invalid Unicode escape / surrogate), and the resulting `PostgresException` also becomes a 500.

D-07 says that anything other than a valid object with a string `run_id` is "a 400 and is not stored". Any authenticated agent can therefore trigger a 500 with an ERROR log at will. That breaks the API contract and is a cheap way to flood error alerting.

**Fix:**
```csharp
private static bool TryGetCleanString(JsonElement v, out string? value)
{
    value = null;
    try { value = v.GetString(); }
    catch (InvalidOperationException) { return false; }      // invalid UTF-16 escape
    return value is not null && !value.Contains('\0');
}

// TryParameters: walk the subtree and validate every string and property name the same way, e.g.
private static bool AllStringsValid(JsonElement e) => e.ValueKind switch
{
    JsonValueKind.Object => e.EnumerateObject().All(p => IsWellFormed(p.Name) && AllStringsValid(p.Value)),
    JsonValueKind.Array  => e.EnumerateArray().All(AllStringsValid),
    JsonValueKind.String => TryGetCleanString(e, out _),
    _ => true,
};
// IsWellFormed: reject '\0' and unpaired surrogates (char.IsSurrogate checks)
```
Use these checks in place of the `\\u0000` substring test. Add a test that posts `"\ud800"` in `run_id`, in an optional field, in a `parameters` value and in a `parameters` key, and asserts 400 `invalid_request`.

### CR-02: Out-of-range numbers in `parameters` still return 500 internal_error with an ERROR log; near-limit numbers amplify the stored data on GET — RESOLVED

**Resolution (re-review 2026-10-21T23:32:28Z):** Fixed in plan 01-11, commit `c9bdca5`.
- **Walk:** `AllStringsValid` now has a `JsonValueKind.Number` case that calls `IsNumberInRange` (`ProposalEndpoints.cs:231-266`). It rejects:
  - raw text over 64 characters;
  - exponents with zero or more than 6 digits, or outside -308..308;
  - any body whose running expansion budget (mantissa digits + |exponent|) exceeds 65,536.
- **No 22003 path remains:** every number that passes is far inside numeric's range (see "How I verified this" above). Amplification on GET is bounded by the budget.
- **Backstop:** `PostAsync` catches a `PostgresException` whose `SqlState` starts with `22` around `InsertAsync` and returns 400 `invalid_request` (`ProposalEndpoints.cs:77-86`). It never echoes the message, and other SQL states still reach the 500 handler.
- **Tests:**
  - `API_01_out_of_range_numbers_give_400_and_store_nothing` covers 1e131072, 1e-16384, -1e999999, a nested array, a 400-digit mantissa, 1e131071 and an over-budget array of 20,000 values of 1e308. It asserts no row, no insert event and no Error log.
  - `API_01_valid_numbers_and_strings_still_accepted` covers the guard against over-rejection.
  - `API_01_class_22_database_error_maps_to_400` and `API_01_non_class_22_database_error_still_gives_500` pin the backstop, using a throwing store double whose message must not leak.
- **Residuals:** the budget over-rejects valid input (WR-10). The backstop logs nothing at all (WR-11).

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:238-239,262` (with `src/Olive.Persistence/ProposalSql.cs:8`, `src/Olive.Host/Problems/ProblemExceptionHandler.cs:12-15`)

**Issue:**
- **The gap:** `AllStringsValid` returns `true` for every `JsonValueKind.Number` (`default: return true;`). `System.Text.Json` validates only the number grammar, so the token reaches `@parameters::jsonb` unchanged through `GetRawText()`. PostgreSQL's numeric type limits the exponent range, and rejects values outside it.
- **Probe results (PG 17):**
  - `{"x":1e131072}`, `{"x":1e200000}`, `{"x":1e-16384}` and `{"x":-1e999999}` all fail with `22003 value overflows numeric format`.
  - `System.Text.Json` parses every one of them without error.
- **Who can trigger it:** `{"run_id":"r","parameters":{"x":1e131072}}` from any allowlisted agent produces a `PostgresException`. `ProblemExceptionHandler` maps it to 500 and calls `LogError`. This is the same D-07 contract break and alert-flood vector as CR-01, in a branch the CR-01 walk did not cover.
- **Amplification:** values just inside the limit are accepted and expand when read back.
  - `{"a":1e131071,"b":1e131071}` is 31 bytes on the wire and 50 bytes in jsonb, but `parameters::text` in `ProposalSql.Get` is 262,158 characters.
  - A 1 MiB body holding about 60k such keys would render to several GB of text on `GET /v1/proposals/{id}`.
  - That exceeds PostgreSQL's 1 GB text limit, or else exhausts host memory in Npgsql and `JsonDocument.Parse` at line 278. The proposal becomes permanently unreadable (GET returns 500), and any tenant agent can store such a row.

**Fix:** Bound numbers in the walk, and add a store-level backstop:
```csharp
case JsonValueKind.Number:
    return IsBoundedNumber(e);

private const int MaxNumberLength = 64;
private static bool IsBoundedNumber(JsonElement e)
{
    var raw = e.GetRawText();
    if (raw.Length > MaxNumberLength) return false;
    var i = raw.IndexOfAny(['e', 'E']);
    if (i < 0) return true;
    return int.TryParse(raw.AsSpan(i + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exp)
        && exp is >= -308 and <= 308;   // same as IEEE double; well inside numeric's range
}
```
Defence in depth: in `PostgresProposalStore.InsertAsync`, catch `PostgresException` with a class-22 `SqlState` (data exception) and surface it as a client fault that the endpoint maps to 400 `invalid_request`, rather than letting it reach the 500 handler. Add `1e131072`, `1e-16384` and an over-length mantissa to `LoneSurrogateBodies` (or `InvalidBodies`), and assert `!factory.Logs.HasErrorOrAbove`.

## Warnings

### WR-01: `GET /v1/proposals/{id}` accepts any active principal, including application (service) credentials and non-allowlisted agents

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:178-181`

**Issue:**
- **What is checked:** `GetAsync` checks only the tenant. `PostAsync` enforces `principal_type == agent` and the allowlist, but GET runs neither check.
- **Who can read:** any `user` or `application` key, and any non-allowlisted agent in the tenant, can read the full proposal: `parameters`, `justification`, `trace_ref`, `on_behalf_of` and `key_id`.
- **Why it matters:** Decision §7 (line 129) and IDN-03 say "olive never accepts a service credential on the proposal endpoint". The test `IDN_02_get_works_for_any_active_key_within_its_tenant` shows the behaviour is deliberate, but no CONTEXT decision (D-01..D-19) authorizes it. It also means a revoked-from-allowlist agent keeps read access to every other agent's proposals in its tenant.

**Fix:** Either record an explicit decision that GET is tenant-scoped for all principal types, or restrict it:
```csharp
var key = http.User.ToActiveKey();
if (key.PrincipalType == PrincipalTypes.Application)
    return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
if (key.PrincipalType == PrincipalTypes.Agent && (key.AgentId is null || !allowlist.IsAllowed(key.AgentId)))
    return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.AgentNotAllowlisted);
```
Then update the identity test to match whichever ruling is made.

### WR-02: `olive_app` can rewrite every column of a submitted proposal, not just `state` — RESOLVED

**Resolution (re-review 2026-10-21T22:57:54Z):** Fixed in plan 01-09, commit `13fde20`.
- **Migration:** `sql/migrations/0002_proposal_column_grants.sql` runs `revoke update on proposal from olive_app` and then `grant update (state) on proposal to olive_app`. 0001 stays checksum-pinned. 0002 is LF and has no BOM.
- **Remaining `olive_app` privileges on `proposal`:** table-level SELECT and INSERT, plus column-level UPDATE(state). There is no DELETE, TRUNCATE, REFERENCES or TRIGGER, because 0001 revokes all from PUBLIC and grants nothing else. `olive_app` is not a member of `olive_owner`; only `olive_migrator` is.
- **Store:** `ProposalSql.UpdateState` writes only `state` (its WHERE columns need SELECT, which is granted), and `Insert` uses `do nothing`, so neither path loses functionality.
- **Tests:**
  - `DB_04_app_update_is_limited_to_state_column` checks `has_column_privilege` for every live column. It is robust to columns added later.
  - `DB_04_app_cannot_update_immutable_proposal_columns` asserts 42501 for sample columns.
  - `DB_04_app_can_still_update_proposal_state` shows the transition path still works.
  - `MigrationRunnerTests` now expects both migrations.
- **Residual (not covered by the UPDATE fix):** INSERT is still table-wide. See WR-09.

**File:** `sql/migrations/0001_init.sql:53`

**Issue:**
- **What the grant allows:** `grant select, insert, update on proposal to olive_app` permits UPDATE of `agent_id`, `key_id`, `on_behalf_of`, `purpose`, `parameters`, `trace_ref`, `run_id` and `key_expires_at`.
- **Why it matters:** the Core Value is that an action "traces to a verified hometown answer" and "runs exactly once". Decision §13 "Immutability" relies on the current row changing only by state transitions, each paired with an INSERT-only `proposal_event`. Any SQL-injection-free but buggy code path, or a compromised `olive_app` session, can silently change what is approved or executed. No event records the change. RLS WITH CHECK stops only cross-tenant moves.
- **Status:** this is in-scope defence in depth. The migration is cheap to fix now and expensive to fix after rows exist.

**Fix:**
```sql
grant select, insert on proposal to olive_app;
grant update (state) on proposal to olive_app;   -- add columns explicitly as later phases need them
```
Extend the privilege-matrix test to assert that `update proposal set parameters = ...` is denied as `olive_app`.

### WR-03: The FND-05 production guard misses decorated stubs, and factory or keyed registrations under non-Olive service types

**File:** `src/Olive.Host/Startup/StartupGuards.cs:36-62` (wiring in `src/Olive.Host/Program.cs:33`)

**Issue:** The guard has two passes, and each has holes.

- **Descriptor scan:** it inspects only `ImplementationInstance` and `ImplementationType`. Factory registrations, which is how Program.cs registers the stub, the allowlist and the data source, are invisible to it.
- **Resolution pass:**
  - It skips keyed services entirely, so a keyed factory registration escapes both passes.
  - It resolves only service types from `Olive.*` assemblies, so a factory-registered fake under a BCL or Microsoft type escapes. Examples are a non-production `TimeProvider` (BCL) or an `IHostedService`.
  - It tests only the outermost resolved instance. A future decorator, such as the introspection cache that D-14 anticipates, would hide the stub: `AddSingleton<IKeyIntrospector>(sp => new CachingIntrospector(StubKeyIntrospector.CreateDefault(...)))` returns an object that is not `INonProductionOnly`, so production starts against fixture keys. That defeats the "fixtures are barred in production" constraint.

**Fix:**
- Drop the `Olive.*` assembly filter and include keyed services in the resolution pass, resolving them with `GetKeyedServices(serviceType, key)`.
- Require decorators of ports to expose their inner component, for example an `IDecorator { object Inner { get; } }` interface, and walk that chain.
- Alternatively, make the composition root register stubs only through one helper (`AddNonProduction<TService, TStub>`) that records them in a list the guard checks.

### WR-04: The REQ-ACT-04 banned-API list leaves other outbound HTTP and socket APIs open

**File:** `BannedSymbols.txt:1-5`

**Issue:** Only `HttpClient`, `HttpClientHandler`, `SocketsHttpHandler`, `IHttpClientFactory` and `WebRequest` are banned. Still usable from Host, Persistence and the Integrations projects:
- `System.Net.WebClient` (a separate type, so banning `WebRequest` does not cover it);
- `System.Net.Http.HttpMessageInvoker`;
- `System.Net.Sockets.TcpClient`, `Socket` and `UdpClient`;
- `System.Net.Security.SslStream`.

The architecture test `FND_02_domain_application_executor_do_not_reference_http_assemblies` covers only Domain, Application and Executor.Simulated. "No outbound HTTP" is therefore not enforced for the projects most likely to grow a connector, which is Integrations.*.

**Fix:** Add the following entries, and extend the architecture assertion to every `src/` project except where Npgsql legitimately needs sockets. Npgsql's internal use is not flagged, because the analyzer checks only source references.
```
T:System.Net.WebClient;REQ-ACT-04: Olive has no outbound HTTP in Phase 1
T:System.Net.Http.HttpMessageInvoker;REQ-ACT-04: Olive has no outbound HTTP in Phase 1
T:System.Net.Sockets.TcpClient;REQ-ACT-04: no raw outbound sockets
T:System.Net.Sockets.Socket;REQ-ACT-04: no raw outbound sockets
T:System.Net.Sockets.UdpClient;REQ-ACT-04: no raw outbound sockets
```

### WR-05: The CI vulnerable-package gate fails open when `dotnet list` itself fails — RESOLVED

**Resolution (re-review 2026-10-21T22:57:54Z):** Fixed in plan 01-10, commit `ffe54fd`.
- **Shell:** the step now declares `shell: bash` (GitHub runs it as `bash --noprofile --norc -eo pipefail`) and also runs `set -euo pipefail`.
- **Exit status:** a non-zero exit from `dotnet list` now fails the step even through `| tee`. The two `grep -c ... || true` substitutions are safe under `-e`, and `grep -q` inside `if` does not trip `-e`.
- **Empty or unrecognized output:** when there are zero recognized project lines, for example because a localized runner prints different text, the step fails closed.
- **Residual:** the "projects == clean" comparison that was added cannot detect a partial report. See WR-08.

**File:** `.github/workflows/ci.yml:39-45`

**Issue:**
- **Shell:** without an explicit `shell:`, GitHub runs `bash -e {0}`, which has no `pipefail`. In `dotnet list package --vulnerable ... | tee vulnerable.txt`, the exit status is the status of `tee`.
- **Fail-open case:** if `dotnet list` fails (vulnerability feed unreachable, NuGet source error, assets problem), the step greps for "has the following vulnerable packages", finds nothing, and passes.
- **Result:** a security gate that silently succeeds when it could not run.

**Fix:**
```yaml
      - name: Vulnerable package gate
        shell: bash
        run: |
          set -euo pipefail
          dotnet list package --vulnerable --include-transitive 2>&1 | tee vulnerable.txt
          if grep -q "has the following vulnerable packages" vulnerable.txt; then
            echo "Vulnerable packages found" >&2; exit 1
          fi
```
Alternatively, use `--format json` and assert on the parsed result.

### WR-06: `PostgresUrl` rejects standard `sslmode=verify-full|verify-ca` and silently drops other TLS parameters

**File:** `src/Olive.Persistence/PostgresUrl.cs:46-58`

**Issue:**
- **Strongest modes rejected:** `Enum.TryParse<SslMode>` matches enum names (`VerifyFull`, `VerifyCA`), not libpq spellings. The standard URL `postgresql://...?sslmode=verify-full` therefore throws `FormatException`, so the only accepted ways to request certificate verification are non-standard spellings. Operators get pushed toward `require` or `prefer`, which do not verify the server. With no `sslmode`, Npgsql's default is `Prefer`, which allows silent downgrade to plaintext.
- **Numeric strings accepted:** `Enum.TryParse` also accepts numeric strings such as `sslmode=7`, producing an undefined `SslMode` value.
- **Other parameters dropped:** every other query parameter is silently ignored, for example `sslrootcert`, `sslcert`, `target_session_attrs` and `options`. An operator who sets `sslrootcert` believes the CA is pinned when it is not.

**Fix:**
```csharp
private static readonly Dictionary<string, SslMode> SslModes = new(StringComparer.Ordinal)
{
    ["disable"] = SslMode.Disable, ["allow"] = SslMode.Allow, ["prefer"] = SslMode.Prefer,
    ["require"] = SslMode.Require, ["verify-ca"] = SslMode.VerifyCA, ["verify-full"] = SslMode.VerifyFull,
};
// sslmode: look up in SslModes, else FormatException.
// sslrootcert: builder.RootCertificate = value.
// Any other key: throw FormatException (fail closed) rather than ignore.
```
Consider also refusing any value weaker than `verify-full` when `OLIVE_ENVIRONMENT=production`.

### WR-07: Migration file names are not validated: a missing underscore crashes the migrator, and duplicate versions misbehave

**File:** `src/Olive.Persistence/MigrationFile.cs:8`, `src/Olive.Persistence/MigrationCatalog.cs:19-26`, `src/Olive.Persistence/MigrationRunner.cs:32`, `src/Olive.Persistence/DatabaseSafetyChecks.cs:49`

**Issue:**
- **Missing underscore:** `Version => FileName[..FileName.IndexOf('_')]` throws `ArgumentOutOfRangeException` for a file such as `0002.sql`, because `IndexOf` returns -1. `MigratorCommand` catches only Format, Checksum, Drift, Npgsql and InvalidOperation exceptions, so the migrator dies with an unhandled-exception stack trace and an undefined exit code.
- **Duplicate versions:** `0002_a.sql` and `0002_b.sql` give the same version.
  - In the runner, `0002_a` is applied, then `0002_b` executes its DDL and fails only on the `schema_migrations` primary key. It rolls back, but the exit code is 1 with a generic message.
  - In `DatabaseSafetyChecks`, `ToDictionary` throws, so the service refuses with the misleading "could not be checked".
- **Ordering:** order is by ordinal file name, and nothing enforces the DB-02 `NNNN_` shape, so `10_x.sql` sorts before `2_x.sql`.

**Fix:** Validate in `MigrationCatalog.LoadEmbedded` and fail with a clear `InvalidOperationException`:
```csharp
[GeneratedRegex("^[0-9]{4}_[a-z0-9_]+\\.sql$")] private static partial Regex NamePattern();
// for each name: if (!NamePattern().IsMatch(file)) throw new InvalidOperationException("Invalid migration file name.");
// after loading: if (files.GroupBy(f => f.Version).Any(g => g.Count() > 1)) throw new InvalidOperationException("Duplicate migration version.");
// optionally: versions must be contiguous from 0001.
```
Add an architecture test that every `sql/migrations/*.sql` file matches the pattern.

### WR-08: The new "report incomplete" count check in the vulnerable gate is tautological and cannot detect a partial report — RESOLVED

**Resolution (re-review 2026-10-21T23:32:28Z):** Fixed in plan 01-13, commit `42e142b`.
- **Script:** the gate logic now lives in `build/vulnerable-gate.sh` (LF, `set -euo pipefail`). It takes `<report> <expected-count> [dotnet-exit-code]` and fails closed in each of these cases:
  - a bad argument count;
  - an expected count that is not a positive integer;
  - a non-zero or non-numeric dotnet exit code;
  - a missing or empty report;
  - any "has the following vulnerable packages" line;
  - a "has no vulnerable packages" count that differs from the expected count.
- **Workflow:** `ci.yml` takes the expected count from `dotnet sln Olive.sln list`, and passes it to the script along with `${PIPESTATUS[0]}` captured under `set +e`.
- **Removed:** the tautological `projects`/`clean` comparison and the dead `-z` test are gone.
- **Tests:** `VulnerableGateScriptTests` runs the real script against canned reports: all clean, one vulnerable, empty, a missing project, an extra project, a non-zero dotnet exit, a non-numeric count, a zero count and a missing file.
- **Live check:** verified against the live repo (12 of 12, exit 0).
- **Residuals:** the failure tests do not tell reasons apart, and the expected count shares a source with the report. See IN-13. The question of whether NuGet prints the "clean" wording when the vulnerability resource is unreachable remains unverified, as before.

**File:** `.github/workflows/ci.yml:48-53` (locked in by `tests/Olive.Tests/CiAndDevConfigTests.cs:53-84`)

**Issue:**
- **Same lines counted twice:** `projects` counts lines containing "The given project", and `clean` counts lines containing "has no vulnerable packages". In real output (verified on this repo, 12 projects), both phrases appear on the same line: "The given project `X` has no vulnerable packages given the current sources." A project that has vulnerabilities is reported as "Project `X` has the following vulnerable packages", which does not contain "The given project", and the earlier grep has already exited for it. So `projects != clean` is never true, and the only working check is `projects == 0`.
- **Dead condition:** `[ -z "$projects" ]` can never be true, because `grep -c` always prints a number.
- **Undetected cases:** the gate never compares against the expected set of projects. It still passes in these cases:
  - A project is dropped from the report while `dotnet list` exits 0, for example a project that is missing from `Olive.sln`, or a NuGet version that skips a project with a warning.
  - The vulnerability resource is unavailable but NuGet still prints the "no vulnerable packages given the current sources" wording with exit 0. I did not verify this; the wording itself hedges on "the current sources".
- **Text-only test:** `FND_07_vulnerable_gate_fails_closed` asserts the literal script text, including the tautological comparison. It never runs the script against sample outputs, so it confirms the implementation exists rather than that the gate fails closed.

**Fix:** Compare against the solution's project count, or switch to the JSON report:
```bash
set -euo pipefail
expected=$(dotnet sln Olive.sln list | grep -c '\.csproj$')
dotnet list package --vulnerable --include-transitive --format json > vulnerable.json
jq -e --argjson n "$expected" '
  (.problems // [] | length) == 0
  and (.projects | length) == $n
  and all(.projects[]; ((.frameworks // []) | all(.[]; ((.topLevelPackages // []) + (.transitivePackages // [])) | length == 0)))' vulnerable.json
```
Move the script to `build/vulnerable-gate.sh` and test it against canned outputs: all clean, one vulnerable, empty, a missing project, and a non-zero exit. Do not pattern-match the YAML.

### WR-09: `olive_app` can still INSERT a proposal in any state (e.g. `approved`) with a forged `created_at`, bypassing the state machine and event pairing — RESOLVED

**Resolution (re-review 2026-10-21T23:32:28Z):** Fixed in plan 01-12, commit `2e3f553`.
- **Migration:** `sql/migrations/0003_proposal_insert_columns.sql` runs `revoke insert on proposal from olive_app` and then grants INSERT on exactly the 14 application-supplied columns. `state` and `created_at` are excluded, so they always take their defaults (`'proposed'`, `now()`). 0001 and 0002 are untouched.
- **Store:** `ProposalSql.Insert` no longer names `state`.
- **Tests:**
  - `DB_04_app_cannot_insert_non_default_state` and `DB_04_app_cannot_insert_created_at` expect 42501.
  - `DB_04_app_insert_is_limited_to_application_columns` checks `has_column_privilege(..., 'INSERT')` against every live column.
  - `DB_04_app_can_insert_proposal_with_default_state` seeds the row as `olive_app` and reads back `proposed`.
  - The table-level matrix now expects only SELECT for `olive_app` on `proposal`.
  - `MigrationRunnerTests` pins the 0003 checksum and file name.
- **Residuals:**
  - `UPDATE (state)` still permits any jump between states with no paired event. This is a later-phase concern, as noted at first report.
  - `proposal_event` INSERT is still table-wide. See IN-12.

**File:** `sql/migrations/0002_proposal_column_grants.sql:3-4` (table-level INSERT from `sql/migrations/0001_init.sql:53`), `src/Olive.Persistence/ProposalSql.cs:6-9`

**Issue:**
- **What remains open:** 0002 restricts UPDATE to `state`, but INSERT is still granted on every column. A buggy code path or a compromised `olive_app` session can run `insert into proposal (..., state, created_at, ...) values (..., 'approved', '2020-01-01', ...)`, which creates an already-approved (or `executed`) proposal. The only constraint on `state` is the vocabulary CHECK, and no `proposal_event` is required.
- **Why it matters:** this is the same Decision §13 and Core Value hole that WR-02 described ("approved by a named reviewer ... provable from the execution record"), reached through INSERT instead of UPDATE. The WR-02 fix closes only half of it. The new `DB_04_*` tests do not cover INSERT.
- **Transitions:** UPDATE(state) also permits any jump, for example `proposed` to `executed`, without a paired event. That is more of a later-phase concern, but it is worth noting now while the migration is fresh.

**Fix:** Restrict INSERT to the columns the application legitimately supplies, so `state` and `created_at` always take their defaults, and drop `state` from `ProposalSql.Insert`:
```sql
-- 0003_proposal_insert_columns.sql
revoke insert on proposal from olive_app;
grant insert (tenant_id, proposal_id, agent_id, key_id, on_behalf_of, purpose, key_expires_at,
              nono_session_id, run_id, action_type, party_ref, parameters, justification, trace_ref)
    on proposal to olive_app;
```
Add `DB_04_app_cannot_insert_non_default_state` (expects 42501 for `insert ... (state) values ('approved')`), and add a `has_column_privilege(..., 'INSERT')` loop like the UPDATE one. Optionally, add a `BEFORE UPDATE OF state` trigger that enforces the allowed transition graph.

### WR-10: The numeric expansion budget charges digits that cannot expand, so valid `parameters` bodies are rejected with 400 (NEW)

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:225-228,255-265`

**Issue:**
- **What is charged:** every number in `parameters` adds `mantissaDigits + |exponent|` to the budget, including plain integers and decimals that have no exponent. Those numbers cannot expand: their jsonb text form is no longer than their wire form. Only an exponent can make the stored text longer than what was received.
- **Valid bodies rejected:**
  - A well-formed object holding about 65,537 single-digit numbers (`{"v":[1,1,1,...]}`, roughly 131 KB, well under the 1 MiB body limit) is rejected with 400 `invalid_request`.
  - So is 6,554 ten-digit IDs, or any mix of numbers totalling more than 65,536 digits.
- **Other hard limits:**
  - A plain decimal longer than 64 characters (a high-precision amount, or a 70-digit identifier sent as a number) is refused.
  - So is any exponent outside +/-308, for example a subnormal such as `4.9e-324`.
- **Why it matters:** D-07 says Phase 1 "stores it as received" and that "checks are minimal". These limits are not in D-07, the API contract or the problem `detail`, and the client cannot tell them apart from a malformed body. No test pins this behaviour either way: the positive set tops out at 100 x `1e308`.

**Fix:** Charge only the expansion beyond the wire size, so non-exponent numbers are free and amplification stays bounded. Then record the remaining limits in the contract or decision log:
```csharp
// cost = digits the jsonb text can add beyond the raw token
var expanded = mantissaDigits + Math.Abs(exponent);
budget += Math.Max(0, expanded - raw.Length);
return budget <= MaxNumberExpansionBudget;
```
Add a positive test with, for example, 70,000 small integers (expect 201) next to the existing over-budget `1e308` body (expect 400).

### WR-11: The class-22 backstop swallows server-originated data faults silently and blames the client (NEW)

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:82-86`

**Issue:**
- **What it catches:** the filter matches any `PostgresException` in class 22 thrown anywhere inside `InsertAsync`. That covers the `set_config` call, the proposal insert and the `proposal_event` insert. It is not limited to faults caused by body content.
- **Values the client never sent:** several inserted values come from the introspected key, not the body: `agent_id`, `key_id`, `on_behalf_of`, `purpose` and `key_expires_at`. A live popeye resolver that returns, for example, a NUL in `on_behalf_of` makes PostgreSQL raise 22021 ("invalid byte sequence for encoding UTF8: 0x00"). The agent then gets 400 `invalid_request` for a body that was valid. Later columns with `varchar(n)` (22001) or date limits (22008) behave the same way.
- **Nothing is logged:** the handler logs nothing at any level, not even the SQL state. Operators get no signal that a resolver or schema defect is rejecting traffic.
- **Missing test:** the test only asserts that no Error log is written, so a "log nothing at all" regression could not be distinguished from the intended behaviour.

**Fix:** Keep the 400, but log the SQL state at Warning (never the message, which can echo values), and narrow the filter to the codes the body can cause:
```csharp
catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.NumericValueOutOfRange      // 22003
                                     or PostgresErrorCodes.InvalidTextRepresentation          // 22P02
                                     or PostgresErrorCodes.UntranslatableCharacter            // 22P05
                                     or PostgresErrorCodes.CharacterNotInRepertoire)          // 22021
{
    logger.LogWarning("Proposal insert refused by database data check {SqlState}", e.SqlState);
    return Invalid();
}
```
Alternatively, map the fault in `PostgresProposalStore.InsertAsync` (the store-level option in the CR-02 fix) and validate key-derived strings with `IsWellFormed` at the auth boundary.

## Info

### IN-01: Duplicate JSON keys are silently accepted, and the last value wins

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:51,122`

**Issue:** `{"run_id":"a","run_id":"b"}` stores `b`. Duplicate keys inside `parameters` are deduplicated by jsonb, which also keeps the last. D-07 defers duplicate-key rejection to Phase 2, so this is recorded only to make sure it is not forgotten. It is a parser-differential risk once trace and hash checks exist.

**Fix:** In Phase 2, set `JsonDocumentOptions.AllowDuplicateProperties = false` (.NET 10) or use raw-byte validation.

### IN-02: A migration file with a UTF-8 BOM is executed with a leading U+FEFF

**File:** `src/Olive.Persistence/MigrationFile.cs:12`

**Issue:** `new UTF8Encoding(false).GetString(bytes)` does not strip a BOM; I verified that the resulting string keeps it. A BOM-prefixed migration sends U+FEFF to PostgreSQL, which fails with a syntax error. Windows editors add BOMs easily, and `.gitattributes` normalizes line endings but not BOMs.

**Fix:** Reject a BOM in `MigrationCatalog`, which keeps checksums honest, or add a hygiene test asserting that no `sql/**` file starts with `EF BB BF`.

### IN-03: A client abort or body-read I/O error is reported as 500 and logged as an error

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:98-114`, `src/Olive.Host/Problems/ProblemExceptionHandler.cs:12-17`

**Issue:** `OperationCanceledException` from `RequestAborted`, and connection-reset `IOException` while reading the body, fall through to the 500 path. The handler logs them at Error and then writes to an aborted response. There is no `HasStarted` check either, so if the response has already started, the write throws.

**Fix:** In the handler, return early (without logging at Error) when `httpContext.RequestAborted.IsCancellationRequested` or `exception is OperationCanceledException`, and skip the write when `httpContext.Response.HasStarted`.

### IN-04: The `.env` parser treats inline `# comments` as part of the value

**File:** `src/Olive.Host/Startup/DotEnvFile.cs:31-35`

**Issue:** `OLIVE_ENVIRONMENT=local # dev box` gives the value `local # dev box`, which FND-04 then rejects with a confusing message. The same input in `OLIVE_POSTGRES_URL` corrupts the URL. Most `.env` tooling strips unquoted inline comments.

**Fix:** For unquoted values, cut at the first ` #`, or document the restriction in `.env.example`.

### IN-05: A missing `on_behalf_of` on an agent key is reported as `agent_not_allowlisted`

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:32-35`

**Issue:** The reason code misattributes a malformed or incomplete key as an allowlist problem. The stub already rejects this case as malformed, but a live resolver might not.

**Fix:** Treat an agent key without `agent_id` or `on_behalf_of` as `key_inactive` (401) in the authentication handler, or give it a distinct reason code.

### IN-06: IPv6 hosts are passed to Npgsql with brackets

**File:** `src/Olive.Persistence/PostgresUrl.cs:29`

**Issue:** For `postgresql://u:p@[::1]:5432/olive`, `Uri.Host` returns `[::1]`, brackets included (verified). Npgsql then tries to resolve `[::1]` as a host name.

**Fix:** Use `uri.IdnHost`, or trim `[` and `]` when `uri.HostNameType == UriHostNameType.IPv6`.

### IN-07: `OliveEnvironment.Allowed` is a public mutable array

**File:** `src/Olive.Host/Startup/OliveEnvironment.cs:6`

**Issue:** `public static readonly string[]` can be modified element by element (`Allowed[0] = "x"`), which would weaken the FND-04 allowlist at runtime.

**Fix:** Use `IReadOnlyList<string>` or `FrozenSet<string>`, as `PrincipalTypes.All` does.

### IN-08: CI actions are pinned by tag, not by commit SHA

**File:** `.github/workflows/ci.yml:18,20`

**Issue:** `actions/checkout@v4` and `actions/setup-dotnet@v4` are mutable tags. Also, the "Full suite" step re-runs every unit test that the previous step already ran.

**Fix:** Pin both actions to commit SHAs. Change the second test step to `--filter "Category=Integration"`.

### IN-09: `proposal_event.to_state`, `from_state` and `event_type` have no CHECK constraints

**File:** `sql/migrations/0001_init.sql:28-31`

**Issue:** `proposal.state` is constrained to the vocabulary, but the append-only history table accepts any text. Because events can never be corrected, one bad write is permanent.

**Fix:** Add the same `check (to_state in (...))` constraint (and the same for `from_state is null or ...`), plus an `event_type in ('submitted','transition')` check.

### IN-10: `pg_advisory_unlock` in `finally` can mask the original migration failure

**File:** `src/Olive.Persistence/MigrationRunner.cs:71-77`

**Issue:** If the connection breaks after it opened, so that `State` still reads Open but the socket is dead, `Exec(... unlock ...)` throws. That `NpgsqlException` replaces the real cause, for example a checksum exception, and the migrator exit code changes from 3 to 1.

**Fix:** Wrap the unlock in `try { ... } catch (NpgsqlException) { }`. The session-level lock is released when the connection closes anyway.

### IN-11: CR-01 test set has blind spots: one case is not a regression detector, there is no over-rejection guard, and NUL is not tested outside `parameters` — RESOLVED

**Resolution (re-review 2026-10-21T23:32:28Z):** Fixed in plan 01-11, commit `6fe5ad6`.
- **Case 6:** the `\u0000`-key case moved to `InvalidBodies`.
- **Positive cases:** `API_01_valid_numbers_and_strings_still_accepted` covers emoji in `parameters` and in `justification`, plus a literal backslash-u text.
- **NUL coverage:** `API_01_nul_outside_parameters_gives_400_and_store_nothing` covers `run_id`, all five optional strings and an array element.
- **Test bodies:** the new bodies use raw string literals.
- **Still open, minor:** the "escaped pair" case is a literal UTF-8 emoji rather than a JSON escape (IN-14). The `IsWellFormed` comment (line 179) still does not say that its surrogate branches are a backstop for callers other than the parser.

**File:** `tests/Olive.Tests/ApiProposalTests.cs:129-151` (and `InvalidBodies` at 98-113)

**Issue:**
- **Case 6 cannot fail:** the `"{\"run_id\":\"r\",\"parameters\":{\"\\u0000\":1}}"` case already returned 400 before the fix, through the old substring screen, so it adds no regression power.
- **No positive tests:** nothing checks that the stricter walk still accepts legitimate input.
  - An escaped valid pair such as `"\ud83d\ude00"` in `parameters` and in `justification` should give 201.
  - A literal backslash-u text such as `"\\\\u0000"` should give 201. The old code wrongly rejected it, and the new code correctly accepts it.
  - Without these tests, an over-eager change to `IsWellFormed` (for example rejecting every surrogate) would pass the suite.
- **NUL coverage is thin:** NUL is covered only inside `parameters`. `run_id`, the five optional strings and an array element (`{"a":["\u0000"]}`) have no NUL case. `IsWellFormed` is the only guard on those paths, because `GetString` does not throw for `\u0000` (probed).
- **Unreachable branches:** the surrogate branches of `IsWellFormed` (lines 179-190) cannot be reached for parser-sourced strings, because `GetString` and `Name` throw first (probed). They are harmless defence, but the comment at line 168 suggests they are the active guard.
- **Test bodies:** the bodies are C# regular strings containing `\\u...`. That is correct, but it is easy to break: the `901a804` fix-up shows the same mistake was made once. Consider raw string literals (`"""{"run_id":"\ud800"}"""`) so the JSON escape is unambiguous.

**Fix:** Drop case 6 or move it to `InvalidBodies`. Add the NUL and positive cases above. Add a comment on `IsWellFormed` noting that the surrogate checks are a backstop for non-parser callers.

### IN-12: `olive_app` can still forge `proposal_event.created_at` and insert arbitrary events (NEW, residual of WR-09)

**File:** `sql/migrations/0001_init.sql:54` (`grant select, insert on proposal_event to olive_app`)

**Issue:**
- **What is still open:** after 0003, `proposal.created_at` is server-set, but `proposal_event` INSERT is still table-wide.
- **What a buggy path or compromised `olive_app` session can do:** write events with any `created_at`, `event_type`, `from_state`, `to_state` and `actor`. IN-09 notes that these columns have no CHECK constraints. One example is a back-dated `transition` to `approved` for a proposal that is still `proposed`.
- **Why it matters:** the history table is append-only and can never be corrected, and Decision §13 relies on it as the provable record.

**Fix:** In a 0004 migration, run `revoke insert on proposal_event from olive_app; grant insert (tenant_id, event_id, proposal_id, event_type, from_state, to_state, actor, detail) on proposal_event to olive_app;`. Add the IN-09 CHECKs and a `has_column_privilege` test that mirrors the proposal one.

### IN-13: Gate-script failure tests do not tell reasons apart, and the expected count comes from the same solution the report reads (NEW, residual of WR-08)

**File:** `tests/Olive.Tests/VulnerableGateScriptTests.cs:131-155`, `.github/workflows/ci.yml:43`

**Issue:**
- **Failure reasons not distinguished:** every failure test asserts only exit code 1, and stderr is discarded (`RunBash`, lines 80-81).
  - `FND_07_gate_script_one_vulnerable_exits_1` passes 2 clean lines with an expected count of 3. Deleting the `has the following vulnerable packages` check would still exit 1 through the count mismatch, so the vulnerable branch is never tested on its own.
  - Any unrelated exit-1 failure inside the script, such as a `set -u` error, would also satisfy every failure test.
- **Shared source:** `expected` counts the `.csproj` entries in `Olive.sln`, and `dotnet list package` enumerates the same solution. A project that is never added to the solution is invisible to both counts and is never scanned.
- **Workflow glue untested:** the `PIPESTATUS`/`set +e` lines are still only text-asserted (`CiAndDevConfigTests.cs:66-68`).

**Fix:**
- Make the one-vulnerable case report three clean lines plus one vulnerable line, so the count matches and only the vulnerable check can fail it.
- Assert a distinct stderr message for each case, or give each failure reason its own exit code.
- Optionally, compare `expected` against `git ls-files '*.csproj' | wc -l` so the scan covers every project in the repo.

### IN-14: The "escaped valid surrogate pair" positive case sends a literal emoji, not a JSON escape (NEW)

**File:** `tests/Olive.Tests/ApiProposalTests.cs:193-194`

**Issue:**
- **What the case sends:** the `parameters` case (line 193) and the `justification` case (line 194) contain the raw UTF-8 bytes `F0 9F 98 80` (U+1F600, checked with `od`). Plan 01-11 (and IN-11) asked for the JSON escape `\ud83d\ude00`.
- **What it misses:** the literal form never goes through `System.Text.Json`'s unescape path. A future raw-text screen, such as rejecting `\ud8` substrings the way the old `\u0000` screen did, would wrongly refuse the escaped pair and still pass the suite.

**Fix:** Add cases whose JSON text contains the escape `\ud83d\ude00` in a `parameters` value and in `justification` (inside raw string literals, so the backslashes reach the parser), and keep the literal cases as well. Optionally, read the stored value back and assert that it equals U+1F600.

---

_Reviewed: 2026-10-21T23:32:28Z (second re-review); first re-review 2026-10-21T22:57:54Z; first pass 2026-10-21T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
