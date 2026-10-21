---
phase: 01-foundation-and-walking-skeleton
plan: 04
subsystem: persistence
tags: [postgres, rls, tenant-isolation, dapper, unit-of-work]
requires: [01-02, 01-03]
provides:
  - TenantUnitOfWork (transaction-local olive.tenant_id via set_config, sole tenant-scoped connection entry point)
  - ProposalSql constants with application tenant predicate
  - IProposalStore port, PostgresProposalStore (insert/get/compare-and-set transition)
  - DB_05 and DB_06 tests (layer ablation, unset-tenant failure, pooled reuse)
affects: [01-05, 01-06]
key-files:
  created: [src/Olive.Application/IProposalStore.cs, src/Olive.Persistence/TenantUnitOfWork.cs, src/Olive.Persistence/ProposalSql.cs, src/Olive.Persistence/PostgresProposalStore.cs, tests/Olive.Tests/TenantUnitOfWorkTests.cs, tests/Olive.Tests/AblationDatabase.cs, tests/Olive.Tests/TenantIsolationAblationTests.cs]
requirements-completed: [DB-05, DB-06]
completed: 2026-10-21
---

# Phase 1 Plan 04: Tenant Unit of Work and Proposal Store Summary

Transaction-scoped tenant context (`set_config(..., true)`) plus tenant-predicated SQL, with 19 passing DB_05/DB_06 tests proving each isolation layer blocks cross-tenant read and write when the other is removed.

## Commits
- 4753894: feat: TenantUnitOfWork, ProposalSql, PostgresProposalStore, IProposalStore, DB_05 tests (11 tests)
- c262953: test: AblationDatabase and DB_06 ablation tests (8 tests)

## Notes
- Verification: DB_05 and DB_06 together 19/19 pass; non-integration suite 83/83; Release solution build 0 errors.
- Insert returns null on duplicate via `on conflict on constraint proposal_run_unique do nothing`; no event is written in that case.
- Dapper reads rows into a private Row class (DateTime) and maps to the DateTimeOffset record, since positional record mapping does not convert types.
- Ablation uses a cloned template DB; the RLS-disabled step runs as postgres with `set role olive_owner`. DB_06 tests are in a non-parallel collection `DbAblation`; they do not alter olive_reader attributes, so the DB_01 re-run flip does not affect them.
- Tests pass TestContext.Current.CancellationToken.

## Deviations from Plan
**[Rule 3 - Blocking] Worktree base mismatch:** reset to 86b556b per the worktree check; vocabulary.yaml re-checked out (sha256 60a44f00).

**Sanity-break acceptance checks not run:** the plan asked to temporarily remove the tenant predicate from `ProposalSql.Get` and observe failures. The sandbox permission classifier denied the in-place edit, so it was not performed. Coverage is still structural: `DB_05_sql_constants_carry_tenant_predicate` asserts the literal predicate, and `DB_06_rls_disabled_*` run through the real `ProposalSql`.

## Known Stubs
None.

## Self-Check: PASSED
All seven created files exist; commits 4753894 and c262953 exist.
