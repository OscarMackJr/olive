---
phase: 08-identity-review-queue-kind
plan: 02
subsystem: auth
tags: [service-token, producers, startup-guard]
requires: []
provides:
  - InboundBearerToken (redacting inbound token with TryRead)
  - IServiceTokenVerifier port, ServiceCaller, ServiceTokenResult (Popeye outbound port)
  - StubServiceTokenVerifier plus contracts/service-token fixtures (non-production only)
  - IProducerRegistry, ProducerRegistry, config/producers.yaml, StartupGuards.CheckProducers
affects: [08-03 authentication wiring]
key-files:
  created:
    - src/Olive.Domain/InboundBearerToken.cs
    - src/Olive.Application/IServiceTokenVerifier.cs
    - src/Olive.Application/IProducerRegistry.cs
    - src/Olive.Catalog/ProducerRegistry.cs
    - src/Olive.Integrations.Popeye/StubServiceTokenVerifier.cs
    - config/producers.yaml
    - contracts/service-token/ (15 files)
    - tests/Olive.Tests/ServiceTokenVerifierTests.cs
    - tests/Olive.Tests/ProducerRegistryTests.cs
  modified:
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Startup/StartupGuards.cs
    - src/Olive.Host/Startup/StartupGuardFilter.cs
    - src/Olive.Integrations.Popeye/Olive.Integrations.Popeye.csproj
    - tests/Olive.Tests/ArchitectureTests.cs
    - tests/Olive.Tests/SecretTypeTests.cs
    - tests/Olive.Tests/SourceHygieneTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
    - tests/Olive.Tests/StartupGuardTests.cs
    - tests/Olive.Tests/TestDoubles.cs
key-decisions:
  - "InboundBearerToken is a separate sealed class so the IDN-08 rules keep SessionKey/ServiceToken apart from inbound tokens"
  - "Verifier outage or malformed fixture throws IntrospectionUnavailableException; nothing catches it (fail closed)"
requirements-completed: [IRQ-04, IDN-03]
duration: 25 min
completed: 2026-10-25
---

# Phase 8 Plan 02: Inbound service token and producer registry Summary

Redacting `InboundBearerToken`, a Popeye-marked `IServiceTokenVerifier` with a fixture stub barred in production, and a strict-parsed `config/producers.yaml` registry cross-validated against `queue-kinds.yaml` at startup.

## Tasks

| Task | Commit | Result |
| ---- | ------ | ------ |
| 1. Token type, port, architecture rule | 89f5736 | sealed redacting class, port, IRQ_04 architecture test, secret-type tests |
| 2. Stub verifier, fixtures, FND-05 | 9ff07c1 | stub, 10 token fixtures plus schema/valid/invalid, embedded resource, host registration, production refusal test |
| 3. ProducerRegistry and CheckProducers | d7cdfe7 | registry, producers.yaml, guard wired after CheckQueueKinds, tests |

Full suite: 1451 passed, 0 failed.

## Deviations from Plan

**1. [Rule 3 - Blocking] SourceHygieneTests allowlist** - `FND_06_expose_secret_called_only_in...` fails for any new type calling `ExposeSecret(`; added `Olive.Domain/InboundBearerToken.cs` to its allowlist.

**2. [Rule 3 - Blocking] Production positive controls** - registering the stub verifier in `Program.cs` made every production-environment positive control trip FND-05. Added `PassThroughServiceTokenVerifier` (test double, not INonProductionOnly) in `TestDoubles.cs` and swapped it in within `StartupGuardTests.ProductionSafePorts`, mirroring how other stubs are hidden. A dedicated test proves a raw stub verifier still refuses production.

**3. [Environment]** After `git reset --hard` to the wave base, `contracts/vendored/*` showed as modified (line endings); restored with `git checkout -- <those two files>`. Nothing under `contracts/vendored` is committed.

## Known Stubs

`StubServiceTokenVerifier` is an intentional fixture stub (INonProductionOnly); a real popeye adapter is unratified (assumption A1).

## Self-Check: PASSED

Files and commits 89f5736, 9ff07c1 verified present; full test suite green.
