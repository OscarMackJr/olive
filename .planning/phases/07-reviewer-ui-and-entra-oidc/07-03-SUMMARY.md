---
phase: 07-reviewer-ui-and-entra-oidc
plan: 03
subsystem: ui
tags: [razor-pages, audit, jcs, hash-recompute, rls]
requires: [07-01]
provides:
  - IAuditStore read-only port and PostgresAuditStore (tenant UoW)
  - AuditHashCheck fail-closed proposal_hash recompute
  - /review/items/{id}/audit page
affects: [07-02, 07-04, 07-05, 07-06]
key-files:
  created:
    - src/Olive.Application/IAuditStore.cs
    - src/Olive.Application/AuditHashCheck.cs
    - src/Olive.Persistence/AuditSql.cs
    - src/Olive.Persistence/PostgresAuditStore.cs
    - src/Olive.Host/Pages/Review/Audit.cshtml
    - src/Olive.Host/Pages/Review/Audit.cshtml.cs
    - tests/Olive.Tests/AuditHashCheckTests.cs
    - tests/Olive.Tests/AuditPageTests.cs
  modified:
    - src/Olive.Host/Program.cs
requirements-completed: [UI-05]
duration: ~1h
completed: 2026-10-24
---

# Phase 7 Plan 03: Audit page

A reviewer opens /review/items/{id}/audit and sees the stored hashes, a server-side Jcs recompute indicator (Matches / Does not match, do-not-rely), the simulated execution chain for approved items, and the seq-ordered event history, with field values shown only to holders of the kind's required role. No new endpoint was added.

## Commits
- 062d84e: audit store, SQL, hash recompute, unit tests
- 71032ae: audit page and integration tests

## Verification
`AuditPage|AuditHashCheck` filter: 14 passed. ArchitectureTests, SourceHygiene and ReviewerPagesList: 33 passed. Integration cases cover role holder (fields, 64-hex hashes equal stored columns, Matches, chain with simulated, ascending seq, no detail text), non-role reviewer (hashes and indicator, no justification or parameter values), approve with no ledger row ("No execution recorded yet." via ThrowOnBegin fault), reject (no chain section), tampered justification ("Does not match"), cross-tenant and unknown ids (404), fixture banner, and a grep-style test for no MapGet/MapPost, no write verbs in AuditSql, no DetailJson.

## Deviations from Plan
1. [Rule 3 - Environment] The worktree base was not da8f736 at start; reset to it per the branch-check step. `contracts/vendored/*` were CRLF in the working tree (FND-03 startup refusal); normalized to LF to match the index, nothing committed for them.
2. Plan 02 helpers (page-based claim/decide) do not exist in this wave's base, so tests seed and approve through the existing JSON API helpers (`DecisionHttp`) and view the page with the dev cookie client.
3. The mismatch indicator reuses the existing `flash-error` and `flash-success` CSS classes rather than adding new rules to olive.css (avoids a conflict with plan 02's edits). Unknown/cross-tenant items return a bare `NotFound()` (404, no body) because the shared NotFound page is not in this base.
4. The tamper test updates `proposal.justification` through the admin connection; the table is not trigger-protected, so no replaced IAuditStore was needed.

## Known Stubs
None.

## Threat Flags
None beyond the plan's threat model (T-07-14..18 mitigated as planned).

## Self-Check: PASSED
