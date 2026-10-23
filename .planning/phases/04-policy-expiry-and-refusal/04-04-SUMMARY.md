---
phase: 04-policy-expiry-and-refusal
plan: 04
subsystem: policy-entitlement
tags: [directory, entitlement, fail-closed, fixtures]
requires: [04-03]
provides:
  - IDirectoryGroupResolver read-only [OutboundPort(Directory)] and DirectoryLookup hierarchy
  - DirectoryVerifier (fail-closed, alarms directory_resolver_401/403, directory_response_malformed)
  - PolicyEvaluator.EvaluateEntitlement (intersection of user groups and agent allowlist)
  - FixtureDirectoryGroupResolver (INonProductionOnly) and contracts/directory fixtures
affects: [04-05]
key-files:
  created:
    - src/Olive.Application/IDirectoryGroupResolver.cs
    - src/Olive.Application/DirectoryVerifier.cs
    - src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs
    - contracts/directory/ (README.md, schema, index.json, analyst-alpha.json, analyst-beta.json)
    - tests/Olive.Tests/DirectoryVerifierTests.cs
    - tests/Olive.Tests/FixtureDirectoryTests.cs
  modified:
    - src/Olive.Application/OutboundPortAttribute.cs
    - src/Olive.Application/IOperationalAlarm.cs
    - src/Olive.Domain/PolicyEvaluator.cs
    - src/Olive.Integrations.Entra/Olive.Integrations.Entra.csproj
    - tests/Olive.Tests/PolicyEvaluatorTests.cs
    - tests/Olive.Tests/ArchitectureTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
decisions:
  - "Entitlement passes only if both user groups and agent allowlist contain required_entitlement (ordinal); null/blank/empty on either side refuses."
  - "DirectoryVerifier reuses TraceVerifierOptions.Timeout; exceptions retain type name only."
  - "contracts/directory schema is not in the paired-example Folders list; the invalid case is built inline per plan."
metrics:
  tasks: 2
  completed: 2026-10-22
---

# Phase 4 Plan 04: Directory Entitlement Foundations Summary

Read-only directory port, fail-closed verifier and an entitlement intersection check in the pure evaluator, plus a fixture-backed non-production stub keyed on (tenant, on_behalf_of). No Host wiring (plan 05).

## Commits
- 7e51bd3: port, verifier, alarm codes, EvaluateEntitlement, unit and architecture tests
- 501c659: fixture resolver, contracts/directory, embedded resources, schema test

## Deviations from Plan
- Worktree base was off at start; reset to the specified base per the orchestrator instruction.
- Otherwise none; plan executed as written.

## Threat model
T-04-14..16, 18, 20 mitigated in code and tests (matrix, intersection, fail-closed verdicts, no group names in details, no HttpClient). T-04-17 and T-04-19 are covered at the stub level (tenant-keyed lookup, INonProductionOnly); production guard and endpoint use follow in plan 05.

## Known Stubs
None beyond the intentional fixture resolver (non-production only).

## Verification
`dotnet build` 0 errors; full `dotnet test` 832 passed, 0 failed.

## Self-Check: PASSED
