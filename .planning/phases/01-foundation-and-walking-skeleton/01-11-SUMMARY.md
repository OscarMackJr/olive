---
phase: 01-foundation-and-walking-skeleton
plan: 11
subsystem: api
tags: [validation, json, postgres, gap-closure]
requires: []
provides:
  - Bounded JSON number walk for POST /v1/proposals parameters
  - Class-22 PostgresException backstop mapped to 400
affects: [src/Olive.Host/Endpoints/ProposalEndpoints.cs]
key-files:
  modified:
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - tests/Olive.Tests/ApiProposalTests.cs
decisions:
  - "Backstop catch lives in the endpoint: Olive.Host already has a Npgsql package allowance, so PostgresProposalStore needed no change."
metrics:
  completed: 2026-10-21
---

# Phase 1 Plan 11: Out-of-range numbers give 400 Summary

Parameters numbers are now bounded at the request edge (raw length 64, exponent -308..308, per-request expansion budget 65536), and class-22 database errors map to 400 invalid_request with no Error log.

## Commits
- 6fe5ad6: test(01-11) failing tests (RED)
- c9bdca5: fix(01-11) number bounds and class-22 backstop (GREEN)

## What changed
- `ProposalEndpoints.cs`: `case JsonValueKind.Number` in `AllStringsValid` via `IsNumberInRange` with `MaxNumberChars=64`, exponent range, and `MaxNumberExpansionBudget=65536` (mantissa digits plus abs(exponent), threaded as a ref counter). `store.InsertAsync` wrapped in a catch of `PostgresException` with SqlState starting "22" returning `Invalid()`; the message is never logged or returned.
- `ApiProposalTests.cs`: NUL-key case moved to `InvalidBodies` (IN-11); new theories `API_01_out_of_range_numbers_give_400_and_store_nothing`, `API_01_valid_numbers_and_strings_still_accepted`, `API_01_nul_outside_parameters_gives_400_and_store_nothing`; facts `API_01_class_22_database_error_maps_to_400` and `API_01_non_class_22_database_error_still_gives_500` (throwing store double).
- `OliveApiFactory` already had a `configureServices` hook, so no change was needed.
- `PostgresProposalStore.cs` was not modified (listed in plan files, not needed).

## Verification
RED confirmed (7 number cases and class-22 failed; positive and NUL tests passed). After the fix, ApiProposalTests plus ArchitectureTests: 62 passed. `dotnet build -c Release`: 0 errors.

## Deviations from Plan
None for code. Note: a full `dotnet test` run showed 3 `StartupGuardTests.FND_04_invalid_environment_*` failures that pass when the class is run alone (20/20); they appear to be parallel-run flakiness unrelated to this plan (out of scope, not fixed).

## Self-Check: PASSED
