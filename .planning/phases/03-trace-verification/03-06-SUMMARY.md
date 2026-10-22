---
phase: 03-trace-verification
plan: 06
subsystem: testing
tags: [containment, sentinel-scan, architecture, idn-08, outbound-ports]
requires:
  - phase: 03-04
    provides: refusal branch, explicit test mode
  - phase: 03-05
    provides: innerResolver/innerIssuer on OliveApiFactory, faulting doubles
provides:
  - IKeyIntrospector marked as a popeye outbound port; all three ports recorded
  - Four IDN_08 architecture tests (port graph, integration implementations, hometown source scan, resolver signature pin)
  - Reusable SentinelScan (responses, logs, stored rows; AddSource for Phase 6 JSONL)
  - ContainmentTests across verified, refused, 503, 422 and claims-mismatch paths
  - STATE.md coordination hand-offs
affects: [06-execution, 09-compatibility]
key-files:
  created:
    - tests/Olive.Tests/SentinelScan.cs
    - tests/Olive.Tests/ContainmentTests.cs
  modified:
    - src/Olive.Application/IKeyIntrospector.cs
    - tests/Olive.Tests/TestDoubles.cs
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/ArchitectureTests.cs
    - tests/Olive.Tests/InMemoryLogCapture.cs
    - .planning/STATE.md
requirements-completed: []
requirements-partial: [IDN-08]
completed: 2026-10-22
---

# Phase 3 Plan 06: Containment evidence Summary

The claim "the session key reaches only popeye and the service token only hometown" is now machine-checked at the port graph, in recorded calls, and by a sentinel search over observer-readable surfaces.

## IDN-08 scope caveat (read this)

The sentinel search covered **logs (captured at Trace level), HTTP responses (body and all headers) and stored rows only**. The JSONL execution output does not exist yet. **The JSONL half of IDN-08 / Success Criterion 5 is deferred to Phase 6** (carry-over recorded in ROADMAP), via `SentinelScan.AddSource`. IDN-08 must not be read as fully satisfied. Port-level capture is not wire-level capture (T-03-36, Q19); wire-level is LIVE-03.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1. Mark and record ports; architecture tests | fa669f4 | `[OutboundPort(Popeye)]` on `IKeyIntrospector`; `RecordingKeyIntrospector` appends to shared `OutboundCalls`; four IDN_08 Architecture tests |
| 2. Containment and sentinel tests | 852794e | `SentinelScan`, 8 `ContainmentTests`, a scan self-test, Trace-level counter on `InMemoryLogCapture` |
| 2. STATE.md hand-offs | 3bbf9c3 | Four coordination items added; position/progress/status untouched |

Full suite: 735 tests, 734 passed on the first run. The one failure was the known `StartupGuardTests` flake (`FND_04_invalid_environment_refuses_start_without_echoing_value(null)`); the class alone passed 28/28.

## Verification notes

- Negative check done once: a temporary Hometown-destined interface with a `SessionKey` parameter made `IDN_08_only_popeye_ports_accept_a_session_key` fail; then removed. (Adding the parameter directly to `ITraceResolver` fails to compile because its implementers break, so a throwaway interface was used.)
- The sentinel scenarios register the real `LoggingOperationalAlarm` over the recording alarm, so Critical alarm log lines are inside the scan on the 503 and claims-mismatch paths. Non-vacuity asserts: every port recorded a call, Trace-level lines exist, `Trace verified` appears, alarm codes appear in logs.
- `SentinelScan` failure messages name the source and give length and a 4-character hint only; proved by `SentinelScanSelfTests`.

## Deviations from Plan

**1. [Rule 3] `InMemoryLogCapture` edited** (not in the plan's file list) to count entries per level, needed for the Trace-level non-vacuity test.

**2. `SentinelScan.CollectAsync` shape.** It returns a `SentinelScan` (with `SourceNames`, `AddSource`, `AssertAbsent`) rather than a list of strings, so Phase 6 can add sources before asserting.

**3. Hometown source scan** filters lines that start with `//`, `/*` or `*` only (not trailing comments), to avoid hiding code after a `//` inside a string.

## Known Stubs

None added.

## Threat Flags

None. T-03-32 to T-03-35 mitigated by the named tests; T-03-36 accepted as planned.

## Self-Check: PASSED

Files and commits fa669f4, 852794e, 3bbf9c3 verified present.
