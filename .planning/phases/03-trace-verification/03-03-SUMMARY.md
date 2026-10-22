---
phase: 03-trace-verification
plan: 03
subsystem: api
tags: [trace-verification, fixtures, popeye-stub, hometown-stub, fail-closed]
requires:
  - phase: 03-01
    provides: trace columns, refusal store, problem reasons
  - phase: 03-02
    provides: TraceVerifier, ServiceTokenProvider, CachingTraceResolver, ports
provides:
  - contracts/trace-resolution fixture contract (schema, 8 response/proposal pairs, README)
  - FixtureTraceResolver and strict TraceResolutionResponse parser (Hometown)
  - StubTokenIssuer (Popeye)
  - POST /v1/proposals trace step; GET exposes trace_verification, trace_schema_version, refusal_reason
  - OliveApiFactory TraceMode (AutoFixture default, Explicit) with recording ports
affects: [03-04, 03-05, 03-06]
key-files:
  created:
    - contracts/trace-resolution/ (schema, valid/invalid, index.json, 8 case pairs, README.md)
    - src/Olive.Integrations.Hometown/FixtureTraceResolver.cs
    - src/Olive.Integrations.Hometown/TraceResolutionResponse.cs
    - src/Olive.Integrations.Popeye/StubTokenIssuer.cs
    - src/Olive.Host/Alarms/LoggingOperationalAlarm.cs
    - tests/Olive.Tests/RecordingPorts.cs
    - tests/Olive.Tests/TraceFixtureTests.cs
    - tests/Olive.Tests/StubTokenIssuerTests.cs
    - tests/Olive.Tests/TraceVerifiedEndpointTests.cs
  modified:
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - src/Olive.Integrations.Hometown/Olive.Integrations.Hometown.csproj
    - .env.example
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/StartupGuardTests.cs
    - tests/Olive.Tests/IdentityEndpointTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
key-decisions:
  - "Auto-fixture mode derives facts per request through an AsyncLocal read by a test-only resolver, not a shared fixture table, so concurrent requests sharing a trace_ref cannot overwrite each other"
  - "Every non-Verified verdict returns 503 trace_verification_unavailable and stores nothing; plans 04 and 05 refine this arm"
requirements-completed: [TRC-01, TRC-06, TRC-09, IDN-05]
completed: 2026-10-22
---

# Phase 3 Plan 03: Trace verification, first end-to-end slice Summary

An agent whose fixture trace matches run, tenant, agent and proposal hash now gets a stored proposal with `trace_verification = fixture` and `trace_schema_version`, readable through GET. The popeye token stub is called before the resolver, and only after idempotency and validation.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1. Fixture contract, resolver, token stub | 154ad65 | Schema + 8 response/proposal pairs with hashes matching production Jcs; strict parser; both stubs INonProductionOnly |
| 2. Host wiring, endpoint step, harness | 71ce59e | DI, alarm, OLIVE_TRACE_TIMEOUT_MS (validated at startup), 503 fail-closed arm, GET fields, auto-fixture test mode |
| 3. Accepted-path and production-bar tests | see git log (test(03-03)) | 7 endpoint tests, 4 StartupGuard cases |

Full suite: 682 passed, 0 failed (Docker available).

## Deviations from Plan

**1. [Rule 1/3] Auto-fixture design changed to per-request AsyncLocal.** The plan's middleware called `FixtureResolver.Register` on a shared table. That broke `API_07_concurrent_different_bodies_yield_one_201_and_conflicts` (concurrent bodies share one trace_ref and overwrite each other's hash). A test-only `AutoFixtureTraceResolver` now reads per-request facts from an `AsyncLocal` set by the test-only startup filter; it falls back to `FixtureResolver`. Still test-project only.

**2. [Rule 3] Existing production positive controls in StartupGuardTests** (`..._decorator_over_production_component_starts`, `..._positive_control_without_stub_starts`) now fail because the factory wires the stub issuer and fixture resolver. Added a `ProductionSafePorts` helper swapping in `FakeTokenIssuer` / `FakeTraceResolver` (already in TraceUnitDoubles). `RecordingAlarm` already existed in `TraceUnitDoubles.cs`, so I reused it instead of adding another.

**3. Plan item not delivered: `TRC_01_null_session_key_returns_503_and_stores_nothing`.** The endpoint has the null-session-key 503 guard, but it is unreachable through HTTP: the authentication handler uses the same `AgentKeyHeader.TryRead` and returns 401 first, so no request can reach the endpoint with an unreadable key. A test would need to bypass auth. Left untested; the guard is defensive.

**4. IDN_05 principal-type assertion skipped.** `ServiceTokenGrant` carries no principal type, so there is nothing to assert; the stub only issues for active registered agent keys.

## Known flake

`StartupGuardTests` full-suite flake (Phase 1) appeared once as `FND_04_migrator_url_in_configuration_...`; the class passes alone (24/24) and the next full run was green.

## Environment note

After the base reset, `contracts/vendored/*` had CRLF in the worktree and broke FND-03 (18+ host tests). Restored with `git -c core.autocrlf=false checkout --` after deleting the files; nothing committed for them.

## Known Stubs

`FixtureTraceResolver` and `StubTokenIssuer` are intentional non-production stubs (barred in production by the FND-05 guard, proven by tests). Live hometown/popeye are future work (LIVE-02).

## Threat Flags

None beyond the plan's threat register. T-03-13 to T-03-19 mitigations implemented: INonProductionOnly stubs plus caching-decorator walk (tests), single 503 fail-closed arm, value-free logs (no trace_ref or parameters), session key passed only to TraceVerifier, strict seven-field parser, auto-fixture filter only under tests/ (`grep IStartupFilter src` finds only StartupGuardFilter).

## Self-Check: PASSED
