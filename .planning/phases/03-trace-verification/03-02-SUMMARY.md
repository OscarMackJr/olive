---
phase: 03-trace-verification
plan: 02
subsystem: application
tags: [trace-verification, service-token, caching, fail-closed]
requires: []
provides:
  - TraceVerifier five-check core with typed TraceVerdict
  - ServiceTokenProvider with claim cross-checks and bounded cache
  - CachingTraceResolver (positive-only cache)
  - Ports ITraceResolver, ITokenIssuer, IOperationalAlarm, OutboundPortAttribute
affects: [03-03, 03-04, 03-05, 03-06]
key-files:
  created:
    - src/Olive.Domain/TraceVerificationSources.cs
    - src/Olive.Domain/TraceRefusalReasons.cs
    - src/Olive.Application/OutboundPortAttribute.cs
    - src/Olive.Application/ITraceResolver.cs
    - src/Olive.Application/ITokenIssuer.cs
    - src/Olive.Application/ServiceTokenGrant.cs
    - src/Olive.Application/IOperationalAlarm.cs
    - src/Olive.Application/ServiceTokenProvider.cs
    - src/Olive.Application/CachingTraceResolver.cs
    - src/Olive.Application/TraceVerdict.cs
    - src/Olive.Application/TraceVerifier.cs
    - tests/Olive.Tests/ManualTimeProvider.cs
    - tests/Olive.Tests/TraceUnitDoubles.cs
    - tests/Olive.Tests/ServiceTokenProviderTests.cs
    - tests/Olive.Tests/CachingTraceResolverTests.cs
    - tests/Olive.Tests/TraceVerifierTests.cs
  modified:
    - tests/Olive.Tests/SecretTypeTests.cs
metrics:
  completed: 2026-10-22
---

# Phase 3 Plan 02: Trace verification decision core Summary

Logging-free, HTTP-free verifier core: service token obtained first, D17.3 checks applied in order (run, tenant, agent, hash) with first failure winning, positive-only trace cache, and token claims cross-checked against introspection.

## Tasks

1. Ports, constants, redacting `ServiceTokenGrant`, alarm codes (8e89e21)
2. `ServiceTokenProvider` plus `ManualTimeProvider` and doubles (9d62285)
3. `CachingTraceResolver`, `TraceVerdict`, `TraceVerifier` and tests (e51f4c9)

## Deviations from Plan

None in behavior. `TraceVerifierOptions` has a parameterless constructor so the default 5 s timeout applies.

## Deferred Issues

- `VendoredContractTests` (FND_03, 3-4 tests) fail in this Windows worktree because `core.autocrlf=true` converts `contracts/vendored/vocabulary.yaml` to CRLF on checkout. It is environmental and pre-existing, unrelated to this plan; all other non-integration tests pass (392 of 395 before the unrelated failures). The vendored files were not edited or committed.

## Known Stubs

None.

## Self-Check: PASSED

All created files exist and commits 8e89e21, 9d62285, e51f4c9 are on the branch.
