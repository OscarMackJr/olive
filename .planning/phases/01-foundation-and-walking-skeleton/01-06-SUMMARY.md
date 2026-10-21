---
phase: 01-foundation-and-walking-skeleton
plan: 06
subsystem: startup-guards
tags: [startup-filter, fail-closed, rls, dotenv, secret-leakage]
requires: [01-05]
provides:
  - IStartupFilter guards (FND-03, FND-04, FND-05, D-19, DB-07) that run before the server listens
  - DatabaseSafetyChecks (role and migration-set violation codes)
  - In-house .env source below real environment variables
  - FND-06 sentinel leak tests
affects: [01-07]
key-files:
  created:
    - src/Olive.Host/Startup/StartupGuardException.cs
    - src/Olive.Host/Startup/OliveEnvironment.cs
    - src/Olive.Host/Startup/StartupGuards.cs
    - src/Olive.Host/Startup/StartupGuardFilter.cs
    - src/Olive.Host/Startup/ServiceCollectionAccessor.cs
    - src/Olive.Host/Startup/DotEnvFile.cs
    - src/Olive.Host/Startup/DotEnvConfiguration.cs
    - src/Olive.Persistence/DatabaseSafetyChecks.cs
    - tests/Olive.Tests/StartupGuardTests.cs
    - tests/Olive.Tests/DatabaseSafetyTests.cs
    - tests/Olive.Tests/DotEnvTests.cs
    - tests/Olive.Tests/SecretLeakageTests.cs
  modified:
    - src/Olive.Host/Program.cs
    - tests/Olive.Tests/OliveApiFactory.cs
requirements-completed: [FND-03, FND-04, FND-05, FND-06, DB-07]
completed: 2026-10-21
---

# Phase 1 Plan 06: Startup Guards and Secret-Leak Proof Summary

The service now refuses to start, with a static rule-named message, on a bad or missing OLIVE_ENVIRONMENT, a present OLIVE_MIGRATOR_URL, a tampered vocabulary, any INonProductionOnly component in production, a privileged database role, or a migration set that is not exactly the embedded list. Sentinel tests prove the session key never reaches responses, headers, logs or exception output. 218 tests green (Release, full solution).

## Commits
- 79459da: environment, migrator-URL, vocabulary-hash and non-production-component guards (Task 1)
- aeb2bc4: DB-07 role and migration guard (Task 2)
- f71ff47: .env loader and FND-06 tests (Task 3)

## IStartupFilter fallback
Not needed. The `IStartupFilter` runs when the host builds its pipeline, before the server starts, under both `WebApplicationFactory` and the real host. Failures surface as `StartupGuardException` (found by walking InnerException).

## Decisions
- FND-05 uses a descriptor scan (instances, types, keyed) plus resolution of every Olive.* service type from a scope; resolution failure refuses with FND-05 (fail closed).
- DB-07 catches any exception from resolving or connecting and refuses with a static message, so no connection detail can escape; the message lists only fixed violation codes.
- `.env` source is inserted at index 0 of the configuration sources so every real source wins.

## Deviations from Plan
**[Rule 3 - Blocking] Worktree base mismatch:** HEAD base was 08232f4; reset to 5238866 per the worktree check; vocabulary.yaml re-checked out (sha256 60a44f00).

**[Rule 3 - Blocking] OliveApiFactory options:** the factory previously always replaced the introspector with a recording wrapper, which would hide the default stub from the FND-05 guard. Added `replaceIntrospector` and `configureServices` constructor options (plus `environment`, as planned) so tests can exercise the real Program registration.

**Test-count note:** DB_07 has 14 tests (plan asked for 9); FND_03/04/05 filter has 25 tests; leak tests 6.

No Task 3 RED-before-GREEN split was recorded as a separate commit; tests and implementation per task were committed together.

## Known Stubs
None new. `StubKeyIntrospector` remains the only introspector; production therefore cannot start in Phase 1 (intended, covered by the FND-05 negative tests).

## Self-Check: PASSED
All created files exist; commits 79459da and aeb2bc4 are in `git log`; `.env` is gitignored and untracked; no third-party dotenv package.
