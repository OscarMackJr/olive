---
phase: 01-foundation-and-walking-skeleton
verified: 2026-10-21T00:00:00Z
status: gaps_found
score: 5/5 roadmap success criteria verified; 1 plan-level truth partially failed (CR-01)
overrides_applied: 0
gaps:
  - truth: "Plan 01-05 / D-07: anything other than a valid object with a string run_id (and well-typed optional fields) is 400 invalid_request and is not stored"
    status: partial
    reason: "A lone-surrogate JSON escape (e.g. run_id or an optional string field of \"\\ud800\") passes JsonDocument.Parse, then JsonElement.GetString() throws InvalidOperationException, which ProblemExceptionHandler maps to 500 internal_error with an ERROR log. A lone surrogate inside parameters passes the \\u0000 screen and Postgres rejects it, also giving 500. Nothing is stored, but the contract (400) is violated and no test covers it (grep for ud800 in tests finds nothing)."
    artifacts:
      - path: "src/Olive.Host/Endpoints/ProposalEndpoints.cs"
        issue: "lines ~122-174: GetString() not guarded; parameters raw text only screened for \\u0000"
    missing:
      - "Guard GetString() with try/catch InvalidOperationException -> 400 invalid_request"
      - "Walk parameters subtree (names and string values) rejecting unpaired surrogates and NUL"
      - "Test posting \"\\ud800\" in run_id, an optional field, a parameters value and a parameters key, asserting 400 invalid_request"
human_verification:
  - test: "Push the branch and confirm the GitHub Actions workflow (.github/workflows/ci.yml) is green on ubuntu-latest"
    expected: "Locked restore, Release build, unit + architecture + Testcontainers tests pass; vendored hash step and vulnerable-package gate pass"
    why_human: "FND-07 requires an actual green Ubuntu run. No push has happened. Only static tests of the workflow file exist. Tests were verified green locally on Windows only."
---

# Phase 1: Foundation and Walking Skeleton Verification Report

**Phase Goal:** An identified agent can submit a proposal and read it back through a real ASP.NET Core host on Postgres, with tenant isolation enforced by both layers. Every silent-pass trap (superuser tests, pooled tenant settings, leaking secrets, stubs in production) is closed off before any feature work.
**Mode:** mvp (goal is not in User Story format, so verified against the roadmap success criteria as written)
**Re-verification:** No, initial verification
**Status:** gaps_found (one narrow plan-level truth, CR-01) plus one human item (FND-07 CI run)

## Behavioral Evidence

`dotnet test Olive.sln -c Release` was run by the verifier: **226 passed, 0 failed, 0 skipped** (Testcontainers Postgres, real host via OliveApiFactory). The only build output was xUnit1051 analyzer warnings.

## Roadmap Success Criteria

| # | Success criterion | Status | Evidence |
|---|-------------------|--------|----------|
| 1 | Allowlisted liam key POSTs and gets proposal_id and state; row records key_id, on_behalf_of, purpose, tenant_id, expires_at, advisory nono_session_id; user/application get 403 principal_not_permitted; inactive key gets 401 first | VERIFIED | `ProposalSql.cs` insert writes all columns. Tests `API_01_post_then_get_roundtrip_through_real_postgres`, `IDN_04_stored_row_records_key_identity_and_advisory_session_id`, `IDN_04_body_tenant_is_ignored`, `IDN_02_non_agent_or_non_allowlisted_post_gives_403_and_stores_nothing`, `IDN_01_inactive_key_with_invalid_body_is_401_not_400`, `IDN_02_principal_check_precedes_body_validation`, `IDN_01_exactly_one_introspect_precedes_insert`. |
| 2 | GET returns within tenant, 404 cross-tenant; ablation tests as olive_app with two tenants, Max Pool Size=1; predicate removed then RLS removed, other layer still blocks; unset tenant errors | VERIFIED | `API_02_cross_tenant_and_missing_ids_give_identical_404`; `TenantIsolationAblationTests` (pool size 1 via `WithMaxPoolSize`): `DB_06_predicate_removed_rls_blocks_cross_tenant_read/write`, `DB_06_rls_disabled_ablation_is_real_and_predicate_blocks_cross_tenant_read/write`, `DB_06_unset_tenant_raises_on_fresh_connection` and `..._on_reused_pooled_connection`, `DB_06_test_roles_are_neither_superuser_nor_bypassrls`. `TenantUnitOfWork` uses transaction-local set_config; `DB_05_sql_constants_carry_tenant_predicate` lint test. |
| 3 | Migrator applies 0001 as olive_owner, records in schema_migrations, fails on changed applied file; proposal_event INSERT-only for olive_app | VERIFIED | `MigrationRunnerTests` (`DB_02_applies_0001_as_owner_and_records_checksum`, `DB_02_changed_bytes_fail_with_checksum_exception`, concurrent-run and rollback tests); `PrivilegeMatrixTests` (`DB_04_privilege_matrix_equals_expected`, app cannot UPDATE/DELETE proposal_event). |
| 4 | Service refuses to start: OLIVE_ENVIRONMENT unset; production + INonProductionOnly; superuser/BYPASSRLS/owner role; migration mismatch; vocabulary hash mismatch | VERIFIED | `StartupGuardTests` (`FND_04_invalid_environment_refuses_start...`, `FND_05_production_with_default_stub/instance/factory/type...refuses`, `FND_03_tampered_vocabulary_copy_is_refused`); `DatabaseSafetyTests` (`DB_07_superuser/owner_member/bypassrls/owns_public_objects`, behind/ahead/checksum, and host-level refusal tests). `StartupGuards.cs` read and consistent. |
| 5 | CI builds restructured solution with locked packages and runs unit, architecture and Testcontainers tests; Domain/Application/Executor HTTP-client or off-allowlist package fails build; secret types redact | VERIFIED locally, CI run UNCERTAIN | Workflow and static tests exist (`CiAndDevConfigTests`); `ArchitectureTests`, `BannedSymbols.txt` (RS0030 error), `SecretTypeTests`, `SecretLeakageTests`; lockfiles present for every project. Green Ubuntu run not yet observed (see human verification). |

**Score:** 5/5 criteria satisfied in the codebase; criterion 5's CI-execution clause awaits a real run.

## Plan-Level Truths

All plan must_haves sampled against code and tests were verified (role bootstrap, checksum-pinned migrator, redacting sealed secret types, Bearer-header single class, YamlDotNet confined to Catalog, no introspection cache, `.env` loader, static problem texts). One exception:

| Truth | Status | Evidence |
|-------|--------|----------|
| D-07: malformed bodies, including invalid content, give 400 and are never stored | PARTIAL (CR-01) | Lone-surrogate escapes give 500. See gaps. |

## Requirements Coverage

Every ID in the phase requirement list appears in a PLAN `requirements:` field (01: FND-01/02/03/07; 02: DB-01..04; 03: FND-06, IDN-01, IDN-02; 04: DB-05, DB-06; 05: API-01, API-02, IDN-01, IDN-02, IDN-04; 06: FND-03..06, DB-07; 07: FND-07). No orphans: REQUIREMENTS.md maps no other IDs to Phase 1 (API-03..09 are Phase 2/4, IDN-03 is Phase 8, IDN-05+ are Phase 3).

| Requirement | Status | Evidence |
|-------------|--------|----------|
| FND-01 | SATISFIED | 11 src projects, `ArchitectureTests` literal reference graph |
| FND-02 | SATISFIED | BannedSymbols.txt plus architecture tests (see WR-04 for the breadth caveat) |
| FND-03 | SATISFIED | `.gitattributes`, `VendoredContracts`, startup guard tests |
| FND-04 | SATISFIED | `OliveEnvironment`, `FND_04_*` tests |
| FND-05 | SATISFIED for current registrations | `FND_05_*` tests (see WR-03 for future-decorator hole) |
| FND-06 | SATISFIED | `SecretTypeTests`, `SecretLeakageTests` (sentinel across responses, Trace logs, exceptions) |
| FND-07 | NEEDS HUMAN | Workflow exists and is statically tested; no green Ubuntu run yet |
| DB-01 | SATISFIED | `DB_01_*` tests |
| DB-02 | SATISFIED | `DB_02_*` tests |
| DB-03 | PARTIAL, expected (D-03) | proposal and proposal_event have tenant_id, ENABLE+FORCE RLS, no missing_ok. Table-driven tests (`RlsPatternTests`) so Phases 5/6 only append names. Not a gap. |
| DB-04 | PARTIAL, expected (D-03) | proposal_event INSERT-only proven by `PrivilegeMatrixTests`; queue_item_event and execution_ledger arrive in Phases 5/6. Not a gap. |
| DB-05 | SATISFIED | `TenantUnitOfWork`, `DB_05_*` tests incl. only-allowed-files-open-connections lint |
| DB-06 | SATISFIED | `TenantIsolationAblationTests` |
| DB-07 | SATISFIED | `DatabaseSafetyTests` |
| API-01 | SATISFIED | `API_01_*` tests |
| API-02 | SATISFIED | `API_02_*` tests |
| IDN-01 | SATISFIED | `IDN_01_*` tests; fixture-driven stub marked INonProductionOnly |
| IDN-02 | SATISFIED | `IDN_02_*` tests |
| IDN-04 | SATISFIED | `IDN_04_*` tests |

## Code Review Impact (01-REVIEW.md)

- **CR-01 (blocker): does not defeat any ROADMAP success criterion**, but it does violate the plan-level D-07 truth ("anything else is 400, not stored"). Nothing is persisted on the failing path, so no integrity or isolation property is lost; the harm is a 500 plus Error-level log that any authenticated agent can trigger. Treated as a real gap (small fix plus test) rather than waived. It is not covered by an existing test.
- WR-01 (GET open to any active principal, including application keys): deliberate and tested (`IDN_02_get_works_for_any_active_key_within_its_tenant`, research Q4). SC2 and API-02 are met. IDN-03 is Phase 8. It needs an explicit recorded decision or restriction. Not a Phase 1 blocker.
- WR-02 (olive_app can UPDATE every proposal column): does not defeat SC3 (which concerns proposal_event), but weakens the Core Value immutability story. Cheap to fix now (column-level grant), expensive later. Recommend fixing before Phase 2 adds idempotency hashes.
- WR-03 (FND-05 guard misses decorators, keyed or non-Olive service types): SC4 holds for all present registrations, including instance, type, and factory forms (tested). It becomes a real hole when the D-14 introspection cache decorator arrives in Phase 3. Must be addressed before then.
- WR-04 (banned-API list incomplete; architecture test limited to Domain/Application/Executor): SC5 text is met as written (those three projects). The wider REQ-ACT-04 intent is only partly enforced for Integrations.*.
- WR-05 (CI vulnerable-package gate fails open), WR-06 (PostgresUrl sslmode spellings), WR-07 (migration filename validation): none defeats a success criterion. WR-05 weakens the SC5/FND-07 gate and should be fixed before relying on CI. WR-06 matters before any non-local deployment.

## Anti-Patterns

No TBD/FIXME/XXX debt markers were checked beyond reliance on the review's file list; the review reported none. No stubs found in production paths. `StubKeyIntrospector` is intentional, marked INonProductionOnly, and guarded in production.

## Human Verification Required

1. **Green Ubuntu CI run (FND-07).** Push the branch and confirm `.github/workflows/ci.yml` passes on ubuntu-latest (locked restore, Release build, unit, architecture and Testcontainers tests, vocabulary hash, vulnerable gate). Not verifiable locally. Also confirm lockfile and line-ending behavior is identical on Linux, since local results come from Windows.

## Gaps Summary

The phase delivers its goal: all five roadmap success criteria are met by real code and a passing 226-test suite, and DB-03/DB-04 are partial exactly as planned. One narrow gap remains: CR-01 (invalid Unicode gives 500 instead of 400), which violates a plan-level truth and has no test. Recommended closure: fix CR-01 with a test, also apply WR-02 and WR-05 (cheap), and schedule WR-03 before Phase 3. FND-07 stays open until a green Ubuntu run is observed.

---

_Verified: 2026-10-21_
_Verifier: Claude (gsd-verifier)_
