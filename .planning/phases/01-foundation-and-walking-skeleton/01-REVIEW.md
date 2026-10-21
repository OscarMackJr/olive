---
phase: 01-foundation-and-walking-skeleton
reviewed: 2026-10-21T00:00:00Z
depth: standard
files_reviewed: 50
files_reviewed_list:
  - .gitattributes
  - .github/workflows/ci.yml
  - BannedSymbols.txt
  - Directory.Build.props
  - Directory.Build.targets
  - Directory.Packages.props
  - config/agents.yaml
  - sql/bootstrap/001_roles.sql
  - sql/bootstrap/002_database.sql
  - sql/migrations/0001_init.sql
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
findings:
  critical: 1
  warning: 7
  info: 10
  total: 18
status: issues_found
---

# Phase 1: Code Review Report

**Reviewed:** 2026-10-21T00:00:00Z
**Depth:** standard
**Files Reviewed:** 50
**Status:** issues_found

## Narrative Findings (AI reviewer)

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
- **One client-triggerable contract violation.** Invalid UTF-16 in the body produces a 500 instead of a 400.
- **Gaps in the controls that are meant to be backstops:**
  - The FND-05 guard can be bypassed through decorators, factory registrations and keyed registrations.
  - The REQ-ACT-04 banned-API list is incomplete.
  - The CI vulnerable-package gate fails open.
  - The proposal row is fully UPDATE-able by `olive_app`.
- **Unrestricted GET.** `GET /v1/proposals/{id}` is open to any active principal in the tenant, including application (service) credentials.

I verified the JSON behaviours with a scratch .NET 10 probe:
- Duplicate keys: the last value wins.
- A lone `\ud800` escape makes `GetString()` throw `InvalidOperationException`.
- `GetRawText()` passes the lone surrogate through unchanged.
- `UTF8Encoding(false).GetString` keeps a leading BOM.

## Critical Issues

### CR-01: Invalid Unicode in the request body returns 500 internal_error instead of 400, and logs an ERROR

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

### WR-02: `olive_app` can rewrite every column of a submitted proposal, not just `state`

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

### WR-05: The CI vulnerable-package gate fails open when `dotnet list` itself fails

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

---

_Reviewed: 2026-10-21T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
