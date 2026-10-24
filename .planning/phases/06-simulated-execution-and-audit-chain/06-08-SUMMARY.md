---
phase: 06-simulated-execution-and-audit-chain
plan: 08
subsystem: host-startup
tags: [gap-closure, CR-01, startup-guards, execution-sweep]
requires: []
provides:
  - "ExecutionSweep deferred to ApplicationStarted (runs after every StartupGuardFilter guard)"
  - "ExecutionSweep.Completion awaitable task"
affects: [src/Olive.Host/Startup/ExecutionSweep.cs, src/Olive.Host/Program.cs, tests/Olive.Tests]
key-files:
  modified:
    - src/Olive.Host/Startup/ExecutionSweep.cs
    - src/Olive.Host/Program.cs
    - tests/Olive.Tests/ExecutionSweepTests.cs
    - tests/Olive.Tests/OliveApiFactory.cs
decisions:
  - "Guards stay only in StartupGuardFilter; ordering is achieved by ApplicationStarted, no duplicated guard logic"
metrics:
  tasks: 2
  completed: 2026-10-24
---

# Phase 6 Plan 08: Defer execution sweep behind startup guards (CR-01) Summary

ExecutionSweep now registers on `IHostApplicationLifetime.ApplicationStarted` and sweeps in a background task, so a host whose startup guard fails performs no execution.

## Tasks

| Task | Commit | Notes |
|------|--------|-------|
| 1 RED: EXE_04_failing_startup_guard_prevents_sweep_execution | 3e9e1b2 | Failed on old code with state "executed" instead of "approved" |
| 2 GREEN: defer sweep, adapt tests | 73298d0 | ExecutionSweep, Program.cs, ExecutionSweepTests, OliveApiFactory |

## Implementation

- `ExecutionSweep(IServiceProvider, IHostApplicationLifetime)`: `StartAsync` only registers callbacks. The body runs via `Task.Run` bound to `ApplicationStopping`. `Completion` is a TaskCompletionSource that is never faulted, set in a finally block and also on `ApplicationStopping`. Static-text logging and per-tenant/per-proposal catches are unchanged. The misleading comment is replaced.
- `Program.cs`: `AddSingleton<ExecutionSweep>()` plus `AddHostedService(sp => ...)`.
- `OliveApiFactory`: new `awaitSweep` flag (default true); overrides `CreateHost` to wait (60 s cap) for `Completion`, keeping other suites deterministic. The EXE_04 test uses `awaitSweep: false`.
- `ExecutionSweepTests`: `Start` became `StartAsync` (awaits Completion); the fault test awaits Completion after the request.

## Deviations from Plan

- [Rule 3 - Blocking, environment] After `git reset --hard` to the base, the working-tree copies of `contracts/vendored/vocabulary.yaml` and `VENDORED.md` had CRLF endings (core.autocrlf=true), tripping the FND-03 hash guard. Restored with a path-specific `git checkout --` (the repo's .gitattributes forces LF). No content change and nothing committed.
- The EXE_04 test sets `OLIVE_MIGRATOR_URL` to an empty string (the D-19 key; presence even empty refuses start).

## Verification

- `dotnet test tests/Olive.Tests --filter ExecutionSweepTests`: 8/8 passed.
- Full suite run twice: 1208/1208 passed both times, no flaky executor-count assertions.

## Known Stubs

None.

## Self-Check: PASSED
