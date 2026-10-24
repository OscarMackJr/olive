---
phase: 06-simulated-execution-and-audit-chain
plan: 07
subsystem: testing
tags: [demo, beat2, transcript, execution-chain]
requires:
  - phase: 06-03
    provides: post-approve simulated execution
  - phase: 06-06
    provides: ExecutionChainViewTests.ChainSelect and QueryChainAsync
provides:
  - Beat2DemoTests (EVD-02) writing TestResults/beat2-transcript.md
  - build/demo-beat2.sh runner
affects: []
tech-stack:
  added: []
  patterns: [scripted demo as xUnit trait-filtered test]
key-files:
  created:
    - tests/Olive.Tests/Beat2DemoTests.cs
    - build/demo-beat2.sh
  modified: []
key-decisions:
  - "Demo uses a non-sentinel rationale because the chain view row shows it by design; session key and justification sentinel are scanned"
requirements-completed: [EVD-02, EXE-06]
duration: 10min
completed: 2026-10-24
---

# Phase 6 Plan 07: Beat 2 demo Summary

A scripted run submits a fixture-traced proposal, approves it as a dev reviewer, executes in simulation, and prints the exact chain-view SELECT (as olive_reader) with its single row plus the JSONL line, stating that trace verification used fixtures.

## Tasks

1. Beat2DemoTests (demo test and runner-script test, sentinel scan of the transcript): 6e56a92
2. build/demo-beat2.sh (mode 100755): 5b3e6a5

## Deviations from Plan

None - plan executed exactly as written. The default factory stub flow is used with a modified justification (as in Plan 06) because the verified-trace fixture pins the proposal hash.

## Known Stubs

None.

## Threat Flags

None.

## Verification

`bash build/demo-beat2.sh` exits 0 and prints the transcript path. Full `dotnet test tests/Olive.Tests`: 1207 passed, 0 failed.

## Self-Check: PASSED
