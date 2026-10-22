---
phase: 01-foundation-and-walking-skeleton
verified: 2026-10-21T00:00:00Z
status: passed
score: 5/5 roadmap success criteria verified; D-07 plan-level truth now verified (CR-02 closed); FND-07 awaits human CI run
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 5/5 roadmap criteria; D-07 partial (CR-02)
  gaps_closed:
    - "CR-02: out-of-range JSON numbers in parameters now give 400 invalid_request (plan 01-11), with class-22 backstop"
    - "WR-08: CI vulnerable gate extracted to build/vulnerable-gate.sh (plan 01-13)"
    - "WR-09: olive_app INSERT limited to non-defaulted columns by migration 0003 (plan 01-12)"
    - "IN-11: positive over-rejection tests added"
  gaps_remaining: []
  regressions: []
human_verification:
  - test: "Push the branch and confirm .github/workflows/ci.yml is green on ubuntu-latest"
    expected: "Locked restore, Release build, unit + architecture + Testcontainers tests pass; vendored hash step and vulnerable-package gate pass"
    why_human: "FND-07 requires an actual green Ubuntu run. Only static tests of the workflow exist; tests verified on Windows only."
---

# Phase 1: Foundation and Walking Skeleton Verification Report

**Phase Goal:** An identified agent can submit a proposal and read it back through a real ASP.NET Core host on Postgres, with tenant isolation enforced by both layers. Every silent-pass trap is closed off before any feature work.
**Re-verification:** Yes, after gap closure plans 01-11, 01-12, 01-13
**Status:** human_needed (no code gaps; FND-07 CI run outstanding)

## CR-02 closure (checked in code and tests)

- `src/Olive.Host/Endpoints/ProposalEndpoints.cs`: `AllStringsValid` now routes `JsonValueKind.Number` to `IsNumberInRange` (line ~295). That method caps raw length at 64 chars, requires 1-6 exponent digits, bounds |exponent| <= 308, and charges a per-request expansion budget of 65,536. Maximum accepted magnitude is about 10^+-372, well inside PostgreSQL numeric, so 22003 cannot arise from a passing number.
- Backstop at line 82: `catch (PostgresException ... SqlState starts with "22")` returns 400 `invalid_request` without echoing the message.
- Tests in `tests/Olive.Tests/ApiProposalTests.cs`: `API_01_out_of_range_numbers_give_400_and_store_nothing` (1e131072, 1e-16384, nested), `API_01_valid_numbers_and_strings_still_accepted` (positive cases), `API_01_class_22_database_error_maps_to_400`, `API_01_non_class_22_database_error_still_gives_500`.
- CR-02 is CLOSED.

## Behavioral evidence (run by this verifier)

| Check | Result |
|-------|--------|
| `dotnet test Olive.sln --no-restore` (first run) | 276/277, 1 failure in `StartupGuardTests` (stack from `TryStartAsync`, StartupGuardTests.cs:36) |
| `dotnet test Olive.sln --no-build` x2 | 277/277 both times |

The intermittent `StartupGuardTests` host-start failure (previously noted as `FND_04_invalid_environment_*` ObjectDisposedException) reproduced once in three runs. It is a test-host lifecycle flake, not a product fault, but it is a CI-flakiness risk for FND-07 and should be tracked.

## Roadmap Success Criteria

| # | Criterion | Status |
|---|-----------|--------|
| 1 | Allowlisted liam key POSTs and gets proposal_id/state; identity columns recorded; user/application 403; inactive key 401 | VERIFIED |
| 2 | GET in tenant, 404 cross-tenant; two-tenant ablation with pool size 1 | VERIFIED |
| 3 | Migrator as owner with pinned checksum; proposal_event INSERT-only for olive_app | VERIFIED (now three migrations, 0002 limits UPDATE to state, 0003 limits INSERT columns) |
| 4 | Startup refuses on unset/invalid env, production stubs, unsafe DB role, migration/vocabulary mismatch | VERIFIED |
| 5 | CI with locked packages and unit/architecture/Testcontainers tests; banned APIs fail build; secrets redact | VERIFIED locally; Ubuntu run UNCERTAIN (human) |

## Requirements Coverage

All 19 IDs (FND-01..07, DB-01..07, API-01, API-02, IDN-01, IDN-02, IDN-04) appear in PLAN `requirements:` fields; REQUIREMENTS.md maps no other IDs to Phase 1, so there are no orphans. (REQUIREMENTS.md traceability table still shows "Pending" for these; it should be updated by the orchestrator.)

| Requirement | Status |
|-------------|--------|
| FND-01..06 | SATISFIED (FND-05 has known future-decorator hole WR-03, fix before Phase 3) |
| FND-07 | NEEDS HUMAN (green Ubuntu CI run) |
| DB-01, DB-02, DB-05, DB-06, DB-07 | SATISFIED |
| DB-03, DB-04 | PARTIAL by design (D-03); proposal/proposal_event covered, later tables in Phases 5/6 |
| API-01 (incl. D-07 invalid-content contract), API-02 | SATISFIED |
| IDN-01, IDN-02, IDN-04 | SATISFIED |

## Anti-Patterns / Residual Warnings (advisory, non-blocking)

| Item | Severity | Note |
|------|----------|------|
| WR-10 | Warning | Numeric expansion budget charges non-expanding digits, so some valid bodies get 400 |
| WR-11 | Warning | Class-22 backstop logs nothing, hiding server-side data faults |
| IN-12 | Info | olive_app can still forge `proposal_event.created_at` |
| IN-13/IN-14 | Info | Gate-test discrimination; emoji literal rather than escape in positive case |
| StartupGuardTests flake | Warning | Reproduced 1 in 3 runs; CI risk |
| WR-03, WR-04 | Warning | Carried forward, scheduled before Phase 3 |

No TBD/FIXME/XXX markers found in src, sql or .github.

## Human Verification Required

1. **Green Ubuntu CI run (FND-07).** Push and confirm `.github/workflows/ci.yml` passes on ubuntu-latest (locked restore, Release build, all test tiers, vocabulary hash, `build/vulnerable-gate.sh`). Watch for the StartupGuardTests host-start flake.

## Gaps Summary

No code gaps remain. The phase goal and all five roadmap criteria are met by real code and a 277/277 suite (one flaky test observed once). Only FND-07's human-observed CI run is outstanding.

---

_Verified: 2026-10-21_
_Verifier: Claude (gsd-verifier)_

## Human Verification Result

- 2026-10-22: the FND-07 green ubuntu-latest CI run was confirmed by the user (01-HUMAN-UAT.md, 1/1 pass). The status changes from human_needed to passed.
