---
phase: 01-foundation-and-walking-skeleton
verified: 2026-10-21T23:30:00Z
status: gaps_found
score: 5/5 roadmap success criteria verified; 1 plan-level truth (D-07) still partially failed (CR-02, new)
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 5/5 roadmap criteria; D-07 partial (CR-01)
  gaps_closed:
    - "CR-01: lone-surrogate / NUL escapes in run_id, optional strings, parameters values and keys now give 400 invalid_request (plan 01-08)"
  gaps_remaining:
    - "D-07 via out-of-range JSON numbers in parameters (CR-02)"
  regressions: []
gaps:
  - truth: "Plan 01-05 / D-07: anything other than a valid object with a string run_id (and well-typed optional fields and well-formed parameters) is 400 invalid_request and is not stored"
    status: partial
    reason: "CR-02. AllStringsValid returns true for every JsonValueKind.Number (verified in ProposalEndpoints.cs, default branch). Numbers such as 1e131072, 1e-16384, -1e999999 parse in System.Text.Json, reach @parameters::jsonb, and PostgreSQL raises 22003; PostgresException maps to 500 internal_error with an ERROR log. Same D-07 contract break and alert-flood vector as CR-01, in a branch the CR-01 walk does not cover. Near-limit exponents (1e131071) are accepted and expand to ~262k chars each in parameters::text on GET, so a stored row can become unreadable or exhaust memory. Nothing invalid is stored on the 22003 path, but the contract is violated and no test covers it."
    artifacts:
      - path: "src/Olive.Host/Endpoints/ProposalEndpoints.cs"
        issue: "AllStringsValid (lines ~212-240) accepts all numbers unbounded"
      - path: "src/Olive.Persistence/PostgresProposalStore.cs"
        issue: "no class-22 PostgresException to client-fault mapping as a backstop"
    missing:
      - "Bound numbers in the parameters walk (raw length cap and exponent range, e.g. +/-308)"
      - "Backstop: map class-22 PostgresException on insert to 400 invalid_request"
      - "Tests posting 1e131072, 1e-16384 and an over-length mantissa, asserting 400 and no ERROR log; consider a cap on expanded size for near-limit exponents"
human_verification:
  - test: "Push the branch and confirm the GitHub Actions workflow (.github/workflows/ci.yml) is green on ubuntu-latest"
    expected: "Locked restore, Release build, unit + architecture + Testcontainers tests pass; vendored hash step and vulnerable-package gate pass"
    why_human: "FND-07 requires an actual green Ubuntu run. No push has happened. Only static tests of the workflow file exist. Tests were verified green locally on Windows only."
---

# Phase 1: Foundation and Walking Skeleton Verification Report

**Phase Goal:** An identified agent can submit a proposal and read it back through a real ASP.NET Core host on Postgres, with tenant isolation enforced by both layers. Every silent-pass trap (superuser tests, pooled tenant settings, leaking secrets, stubs in production) is closed off before any feature work.
**Mode:** mvp (goal is not in User Story format, so verified against the roadmap success criteria as written)
**Re-verification:** Yes, after gap closure plans 01-08, 01-09, 01-10
**Status:** gaps_found (one narrow plan-level truth, now CR-02) plus one human item (FND-07 CI run)

## Re-verification of Previously Reported Gaps

| Gap | Status | Evidence checked in code |
|-----|--------|--------------------------|
| CR-01 lone surrogate gives 500 | CLOSED | `ProposalEndpoints.cs`: `TryGetCleanString` (line ~153) catches `InvalidOperationException` and runs the well-formedness check; used for `run_id` (127) and optional strings (149); `AllStringsValid` (212) walks property names (`TryGetPropertyName`), nested objects, arrays and string values; `MaxDepth = 64` on `JsonDocument.Parse`. Test `API_01_lone_surrogate_and_nul_escapes_give_400_and_store_nothing` (6 cases) asserts 400, zero rows, no insert event, and no Error log. The reviewer traced 5 of 6 cases as RED against the old code. |
| WR-02 (recommended) olive_app UPDATE on all proposal columns | CLOSED (UPDATE side) | `sql/migrations/0002_proposal_column_grants.sql` revokes UPDATE and grants `update (state)` only; 0001 untouched. DB_04 column-privilege tests added. INSERT side remains as WR-09. |
| WR-05 (recommended) CI vulnerable gate fails open | CLOSED in part | `ci.yml` has `shell: bash`, `set -euo pipefail`, and a zero-projects check. The added `projects != clean` comparison is tautological (WR-08), so only the "empty/unrecognized output" and "dotnet list failed" cases are really fixed. |

## Behavioral Evidence

Orchestrator reports `dotnet build -c Release` clean and `dotnet test` 236/236 passed on rerun, with one intermittent `ObjectDisposedException` in `StartupGuardTests.FND_04_invalid_environment_refuses_start_without_echoing_value` (passed 3/3 isolated reruns). I did not rerun the suite; this is the orchestrator's claim, consistent with the earlier verifier-run 226/226 plus ten new tests from 01-08/01-09. The intermittent failure is a test-host lifecycle flake, not a product behaviour failure, but should be tracked (see Anti-Patterns).

## Roadmap Success Criteria

| # | Success criterion | Status | Evidence |
|---|-------------------|--------|----------|
| 1 | Allowlisted liam key POSTs and gets proposal_id and state; row records identity columns; user/application get 403; inactive key 401 first | VERIFIED | Unchanged from initial verification (API_01, IDN_01/02/04 tests). Endpoint changes in 01-08 only add stricter body validation after the auth and principal checks. |
| 2 | GET within tenant, 404 cross-tenant; ablation tests with two tenants, pool size 1 | VERIFIED | Unchanged; `TenantIsolationAblationTests`, `API_02_*`. |
| 3 | Migrator applies migrations as owner, checksum pinned; proposal_event INSERT-only for olive_app | VERIFIED | `MigrationRunnerTests` now expects 0001 and 0002; `PrivilegeMatrixTests`. 0002 strengthens, does not weaken. |
| 4 | Startup refuses on unset/invalid environment, stubs in production, unsafe DB role, migration or vocabulary mismatch | VERIFIED | Unchanged; `StartupGuardTests`, `DatabaseSafetyTests`. |
| 5 | CI builds with locked packages and runs unit, architecture and Testcontainers tests; banned APIs fail build; secrets redact | VERIFIED locally, CI run UNCERTAIN | Workflow present and statically tested; vulnerable gate is partly vacuous (WR-08). No green Ubuntu run observed. |

## Assessment of the new review findings

| Finding | Classification | Reasoning |
|---------|----------------|-----------|
| CR-02 out-of-range numbers give 500 | BLOCKER at plan level (gap) | Same class as CR-01, which this verification already treated as a gap against D-07/API-01 ("anything else is 400, not stored"). Treating it differently would be inconsistent. No ROADMAP success criterion is defeated and nothing invalid is persisted, but any authenticated agent can force 500s with ERROR logs, and near-limit numbers can make a stored proposal unreadable via GET (availability harm to API-02 read-back). Code confirms the unbounded `Number` branch. Small fix plus test. |
| WR-08 CI count check vacuous | WARNING | Weakens the FND-07 / SC5 vulnerable-package gate but does not defeat it: the grep for vulnerable packages, `pipefail`, and the zero-projects check work. FND-07 already requires a human-observed Ubuntu run. Fix before relying on the gate (script out of YAML, test with canned outputs). |
| WR-09 olive_app INSERT can set `state` / `created_at` | WARNING | DB-04 as scoped (proposal_event INSERT-only) is satisfied, and the roadmap SC3 is met. Phase 1 has no approval path, so no approved/executed state is reachable by product code. It is a Core Value hardening hole (Decision 13) that should be closed by a 0003 column-level INSERT grant before Phase 2/5 add real transitions. |
| IN-11 CR-01 test blind spots | INFO | One non-discriminating case, no positive over-rejection tests. Add with the CR-02 fix. |

## Requirements Coverage

Every ID in the phase requirement list appears in a PLAN `requirements:` field (01: FND-01/02/03/07; 02: DB-01..04; 03: FND-06, IDN-01, IDN-02; 04: DB-05, DB-06; 05: API-01, API-02, IDN-01, IDN-02, IDN-04; 06: FND-03..06, DB-07; 07: FND-07; gap-closure plans 08-10 reference API-01/D-07, DB-04 and FND-07). No orphans: REQUIREMENTS.md maps no other IDs to Phase 1.

| Requirement | Status | Evidence |
|-------------|--------|----------|
| FND-01 | SATISFIED | Architecture reference-graph tests |
| FND-02 | SATISFIED | BannedSymbols.txt plus architecture tests (WR-04 breadth caveat) |
| FND-03 | SATISFIED | Vendored hash and startup guard tests |
| FND-04 | SATISFIED | `OliveEnvironment`, `FND_04_*` (one intermittent flake noted) |
| FND-05 | SATISFIED for current registrations | `FND_05_*` (WR-03 future-decorator hole, fix before Phase 3) |
| FND-06 | SATISFIED | Secret type and leakage tests |
| FND-07 | NEEDS HUMAN | Workflow exists, vulnerable gate partly vacuous (WR-08), no green Ubuntu run yet |
| DB-01, DB-02 | SATISFIED | `DB_01_*`, `DB_02_*` (now two migrations) |
| DB-03 | PARTIAL, expected (D-03) | proposal and proposal_event covered; later tables arrive in Phases 5/6 |
| DB-04 | PARTIAL, expected (D-03), strengthened | proposal_event INSERT-only; proposal UPDATE now limited to `state`; INSERT residual is WR-09 |
| DB-05, DB-06, DB-07 | SATISFIED | `DB_05_*`, `TenantIsolationAblationTests`, `DatabaseSafetyTests` |
| API-01 | SATISFIED for the roadmap criterion; D-07 invalid-content contract partial (CR-02) | `API_01_*` tests |
| API-02 | SATISFIED | `API_02_*` |
| IDN-01, IDN-02, IDN-04 | SATISFIED | `IDN_*` tests |

## Anti-Patterns

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `ProposalEndpoints.cs` AllStringsValid | unbounded Number branch | Blocker (CR-02) | 500 plus ERROR log on client input |
| `.github/workflows/ci.yml` | tautological count comparison | Warning (WR-08) | Gate cannot detect a partial report |
| `0002_proposal_column_grants.sql` / INSERT grant | table-wide INSERT | Warning (WR-09) | Can forge state / created_at |
| `StartupGuardTests.FND_04_invalid_environment_*` | intermittent ObjectDisposedException in host start | Warning | Flaky CI risk; investigate disposal ordering |

No TBD/FIXME/XXX debt markers were reported by the review; no stubs in production paths.

## Human Verification Required

1. **Green Ubuntu CI run (FND-07).** Push the branch and confirm `.github/workflows/ci.yml` passes on ubuntu-latest (locked restore, Release build, unit, architecture and Testcontainers tests, vocabulary hash, vulnerable gate). Also confirm lockfile and line-ending behaviour on Linux, and watch for the FND_04 flake.

## Gaps Summary

The phase goal and all five roadmap success criteria are met by real code and a passing suite. Gap closure plans 01-08 and 01-09 are verified in code (CR-01 closed; proposal UPDATE restricted to `state`), and 01-10 is closed in part. One narrow plan-level gap remains: CR-02, where out-of-range JSON numbers in `parameters` return 500 instead of 400. Recommended closure plan: bound numbers in the walk, add a class-22 backstop mapping to 400, add tests (including IN-11 positive cases). In the same pass, fix WR-08 (script plus canned-output tests) and WR-09 (0003 column-level INSERT grant). Schedule WR-03 before Phase 3. FND-07 stays open until a green Ubuntu run is observed.

---

_Verified: 2026-10-21T23:30:00Z_
_Verifier: Claude (gsd-verifier)_
