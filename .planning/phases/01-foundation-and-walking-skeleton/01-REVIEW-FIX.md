---
phase: 01-foundation-and-walking-skeleton
fixed_at: 2026-10-21T23:59:00Z
review_path: .planning/phases/01-foundation-and-walking-skeleton/01-REVIEW.md
iteration: 1
findings_in_scope: 7
fixed: 6
skipped: 1
status: partial
---

# Phase 1: Code Review Fix Report

**Fixed at:** 2026-10-21T23:59:00Z
**Source review:** .planning/phases/01-foundation-and-walking-skeleton/01-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 7 (open Warnings WR-01, WR-03, WR-04, WR-06, WR-07, WR-10, WR-11; no open Critical findings; resolved CR-01, CR-02, WR-02, WR-05, WR-08, WR-09 and all Info findings were out of scope)
- Fixed: 6
- Skipped: 1
- Full suite after fixes: `dotnet test Olive.sln` passed 314/314 (was 277 before; 37 new test cases).

## Fixed Issues

### WR-03: The FND-05 production guard misses decorated stubs, and factory or keyed registrations under non-Olive service types

**Files modified:** `src/Olive.Application/IComponentDecorator.cs` (new), `src/Olive.Host/Startup/StartupGuards.cs`, `tests/Olive.Tests/StartupGuardTests.cs`
**Commit:** eae35e1
**Applied fix:**
- Removed the `Olive.*` assembly filter. The resolution pass now resolves every closed service type, from any assembly.
- Keyed registrations are now resolved too, with `GetKeyedServices(type, key)`. `KeyedService.AnyKey` registrations are skipped because they cannot be resolved; the descriptor scan still checks their implementation types.
- Added the `IComponentDecorator { object Inner }` port. The guard walks each resolved instance's decorator chain and refuses at depth 32 or more, which also covers cycles.
- Resolution failures still fail closed.
- New tests:
  - a stub behind two decorators refuses to start;
  - a keyed factory stub refuses;
  - a factory-registered `INonProductionOnly` `TimeProvider` (a BCL service type) refuses;
  - a decorator over a production component still starts.
- The existing production positive control still starts.

The guard can only see through decorators that implement `IComponentDecorator`. A future decorator that doesn't implement it would still hide a stub. The review's alternative, a single `AddNonProduction` helper, was not adopted.

### WR-04: The REQ-ACT-04 banned-API list leaves other outbound HTTP and socket APIs open

**Files modified:** `BannedSymbols.txt`, `tests/Olive.Tests/ArchitectureTests.cs`
**Commit:** 5faf31e
**Applied fix:**
- Banned `WebClient`, `HttpMessageInvoker`, `TcpClient`, `TcpListener`, `Socket`, `UdpClient` and `SslStream`. RS0030 is already an error.
- Added `REQ_ACT_04_no_src_assembly_references_outbound_network_assemblies`. It reads the assembly references of every `src/` project's built dll through `System.Reflection.Metadata`, which also covers the Migrator (the tests don't reference it). It fails on `System.Net.Http*`, `System.Net.Sockets`, `System.Net.Security`, `System.Net.WebClient` and `System.Net.Requests`.
- Npgsql is a separate assembly, so this check doesn't flag it.
- `FND_02_banned_api_configuration_is_error_level` now also asserts the new entries.

### WR-06: `PostgresUrl` rejects standard `sslmode=verify-full|verify-ca` and silently drops other TLS parameters

**Files modified:** `src/Olive.Persistence/PostgresUrl.cs`, `tests/Olive.Tests/PostgresUrlTests.cs`
**Commit:** f8e898c
**Applied fix:**
- `sslmode` now uses an ordinal map of libpq spellings (`disable`, `allow`, `prefer`, `require`, `verify-ca`, `verify-full`). Numeric values and enum-name values (`7`, `VerifyFull`, `Require`) are rejected.
- `sslrootcert`, `sslcert` and `sslkey` map to `RootCertificate`, `SslCertificate` and `SslKey`. An empty value is rejected.
- Any other query parameter now fails closed with the same static `FormatException`, as do a parameter with no value and a repeated parameter.
- Tests cover every accepted spelling, the TLS file mapping and the new rejection cases.
- Not done: the review's optional "refuse anything weaker than verify-full in production". That is a deployment-policy decision for a human to make.

### WR-07: Migration file names are not validated: a missing underscore crashes the migrator, and duplicate versions misbehave

**Files modified:** `src/Olive.Persistence/MigrationCatalog.cs`, `src/Olive.Persistence/MigrationFile.cs`, `tests/Olive.Tests/ArchitectureTests.cs`
**Commit:** fd972c1
**Applied fix:**
- `MigrationCatalog.LoadEmbedded` now calls a new public `ValidateFileNames`. It enforces `^[0-9]{4}_[a-z0-9_]+\.sql$`, unique versions, and versions contiguous from 0001.
- Failures throw `InvalidOperationException`, which `MigratorCommand` already handles: "Invalid migration file name.", "Duplicate migration version." or "...not contiguous from 0001.".
- `MigrationFile.Version` now throws `InvalidOperationException` instead of `ArgumentOutOfRangeException` when the name has no underscore.
- New tests:
  - every file in `sql/migrations/` passes validation;
  - bad name shapes are refused;
  - duplicate versions are refused;
  - gaps and a missing 0001 are refused;
  - `Version` throws on a name with no underscore.
- No migrations were added or edited. The `MigrationRunnerTests` expectations are unchanged.

### WR-10: The numeric expansion budget charges digits that cannot expand, so valid `parameters` bodies are rejected with 400

**Files modified:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs`, `tests/Olive.Tests/ApiProposalTests.cs`
**Commit:** 021e1aa
**Status:** fixed: requires human verification (logic change)
**Applied fix:**
- Each number now adds `max(0, mantissaDigits + |exponent| - raw.Length)` to the budget, so numbers without an exponent cost nothing.
- New positive cases now return 201: 70,000 single-digit integers, and 7,000 ten-digit integers.
- The existing over-budget case (20,000 x `1e308`, about 6M expansion) still returns 400.
- Not done: the 64-character and +/-308 exponent limits are unchanged and are still not recorded in D-07 or the API contract. Recording them is a documentation and contract decision for a human.

### WR-11: The class-22 backstop swallows server-originated data faults silently and blames the client

**Files modified:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs`, `tests/Olive.Tests/ApiProposalTests.cs`
**Commit:** ab3e495
**Status:** fixed: requires human verification (logic change)
**Applied fix:**
- The backstop now catches only `PostgresErrorCodes.NumericValueOutOfRange` (22003), `InvalidTextRepresentation` (22P02), `UntranslatableCharacter` (22P05) and `CharacterNotInRepertoire` (22021).
- It logs `"Proposal insert refused by database data check {SqlState}"` at Warning. It logs the SQL state only, never the message.
- Every other class-22 code now reaches the 500 handler.
- The class-22 test became a theory over the four codes. It asserts the Warning text includes the SQL state, that the message ("secret-parameter-value") is never logged, and that nothing is logged at Error.
- New theory: 22001 and 22008 return 500.
- The logger comes from an `ILoggerFactory` endpoint parameter under the category `Olive.Host.Endpoints.ProposalEndpoints`, because a static class can't be a `ILogger<T>` type argument.
- Still open: 22021 can also come from a key-derived value with a NUL. That case still returns 400, but now logs a Warning. The review's alternative, validating key-derived strings at the auth boundary, was not applied.

## Skipped Issues

### WR-01: `GET /v1/proposals/{id}` accepts any active principal, including application (service) credentials and non-allowlisted agents

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:325-343` (`GetAsync`)
**Reason:** this needs a human authorization-policy decision, so I didn't change the code.
- **The review's options:** record an explicit decision that GET is tenant-scoped for every principal type, or restrict GET.
- **The current behaviour is deliberate:** the test `IDN_02_get_works_for_any_active_key_within_its_tenant` pins it, and API-02 says "a caller ... within its own tenant".
- **Restricting needs a ruling first:**
  - Decision §7 and IDN-03 are the governing text, and IDN-03 is scheduled for Phase 8.
  - Restricting GET means choosing which principal types may read proposals and adding new 403 responses to the endpoint. That is a decision outside D-01..D-19.
  - The 403 responses may also affect the IR-1 consumer contract.
- **Next step:** a human should record the ruling in the CONTEXT decision log. Then apply the restriction from the review, or leave the code as it is, and update the IDN-02 test to match.

**Original issue:** `GetAsync` checks only the tenant. Any `user` or `application` key, and any agent that isn't allowlisted, can read every proposal in its tenant, including `parameters`, `justification`, `trace_ref`, `on_behalf_of` and `key_id`.

---

_Fixed: 2026-10-21T23:59:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_

## Orchestrator follow-up

- **WR-04 follow-up (`6661f2a`):** on a clean checkout, `REQ_ACT_04_no_src_assembly_references_outbound_network_assemblies` failed with "Olive.Migrator: built assembly not found". `dotnet test Olive.sln` does not build `Olive.Migrator`, so the fixer's 314/314 depended on a Debug Migrator build that was already on disk. The fix adds a build-order-only `ProjectReference` from the test project to `Olive.Migrator.csproj` (`ReferenceOutputAssembly="false"`). Lockfiles are unchanged and `dotnet restore --locked-mode` passes. Re-verified from a clean state: 314/314. CI was not affected, because it runs `dotnet build -c Release` before `dotnet test --no-build`.
