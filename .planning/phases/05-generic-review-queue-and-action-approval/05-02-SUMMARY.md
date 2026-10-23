---
phase: 05-generic-review-queue-and-action-approval
plan: 02
subsystem: review-queue-config
tags: [queue-kinds, reviewer-tenant-map, startup-guard, yaml]
requires: []
provides:
  - IQueueKindRegistry / QueueKindDefinition (Olive.Application)
  - IReviewerTenantMap (Olive.Application)
  - config/queue-kinds.yaml, config/reviewer-tenants.yaml (embedded)
  - StartupGuards.CheckQueueKinds (D-21)
affects: [later phase 05 plans reading required_role, ref schemes, group to tenant map]
tech-stack:
  added: []
  patterns: [strict YamlDotNet parse with static error messages, embedded config resources]
key-files:
  created:
    - src/Olive.Application/IQueueKindRegistry.cs
    - src/Olive.Application/IReviewerTenantMap.cs
    - src/Olive.Catalog/QueueKindRegistry.cs
    - src/Olive.Catalog/ReviewerTenantMap.cs
    - config/queue-kinds.yaml
    - config/reviewer-tenants.yaml
    - tests/Olive.Tests/QueueKindRegistryTests.cs
    - tests/Olive.Tests/ReviewerTenantMapTests.cs
  modified:
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Startup/StartupGuards.cs
    - src/Olive.Host/Startup/StartupGuardFilter.cs
    - tests/Olive.Tests/StartupGuardTests.cs
requirements-completed: [QUE-02]
duration: 20min
completed: 2026-10-23
---

# Phase 5 Plan 02: Kind registry and reviewer tenant map Summary

Source-controlled, strict-parsed kind registry (action_approval and identity_review as config only) and group-to-tenant map, loaded from DI, with a startup guard that refuses to start when catalog required_approval drifts from the action_approval required_role.

## Commits
- 4070375: registry, tenant map, YAML, unit tests
- 446012d: DI registration, CheckQueueKinds guard, tests

## Deviations from Plan
None in code. The guard reads `required_approval` from `CatalogEntry.EntryJson` because the loaded model has no RequiredApproval property (no catalog model change needed).

## Issues / Deferred
- Windows checkout (autocrlf) leaves `contracts/vendored/vocabulary.yaml` with CRLF, so FND-03 tests and every host-starting integration test fail in a fresh worktree (pre-existing, unrelated). I normalised the working-tree copy to LF locally (not committed) to run the host tests; all 60 targeted tests then passed. `contracts/vendored/VENDORED.md` also shows as locally modified from line endings and was not staged.

## Verification
`dotnet test --filter "QUE_02|StartupGuard"`: 60 passed. QUE_02 / ReviewerTenant / Architecture unit filter passed apart from the pre-existing FND-03 CRLF failures.

## Self-Check: PASSED
