---
phase: 01-foundation-and-walking-skeleton
plan: 01
subsystem: build-foundation
tags: [dotnet, cpm, lockfiles, banned-api, architecture-tests, vendored-contract]
requires: []
provides:
  - 11-project solution skeleton with inward-only reference graph
  - RS0030 banned-API build gate (HttpClient, clock access)
  - VendoredContracts.VerifyVocabulary and pinned vocabulary SHA-256
affects: [01-02, 01-03, 01-04, 01-05, 01-06, 01-07]
tech-stack:
  added: [Npgsql 10.0.3, Dapper 2.1.89, YamlDotNet 18.1.0, BannedApiAnalyzers 5.6.0, xunit.v3 3.2.2, Testcontainers.PostgreSql 4.16.0, Mvc.Testing 10.0.13]
  patterns: [Central Package Management with transitive pinning, packages.lock.json per project, reflection and XML/JSON architecture tests]
key-files:
  created: [.gitattributes, .editorconfig, Directory.Packages.props, BannedSymbols.txt, src/Olive.Application/VendoredContracts.cs, tests/Olive.Tests/ArchitectureTests.cs, tests/Olive.Tests/VendoredContractTests.cs, tests/Olive.Tests/TestPaths.cs]
  modified: [Directory.Build.props, Directory.Build.targets, Olive.sln, tests/Olive.Tests/Olive.Tests.csproj]
key-decisions:
  - "NetArchTest not used; FND-02 via BannedApiAnalyzers plus reflection, csproj and lockfile parsing (Q7)"
  - "Scaffold test REQ_ACT_04_no_external_write_connector_exists retired, replaced by FND_02 tests (Q8)"
requirements-completed: [FND-01, FND-02, FND-03, FND-07]
duration: 25min
completed: 2026-10-21
---

# Phase 1 Plan 01: Foundation Skeleton Summary

11-project .NET 10 solution with CPM and lockfiles, RS0030 making `new HttpClient()` a build error, and FND-01/02/03 enforced by 9 passing named tests.

## Commits
- f4c7aa9 chore: byte-stability rules, CPM, banned-API config
- 59b5c79 feat: restructure into 11 src projects, delete scaffold, lockfiles
- b4752ed test: FND-01/02/03 tests and VendoredContracts

## Notes
- **REQ_ACT_04 retirement:** the scaffold test `REQ_ACT_04_no_external_write_connector_exists` read the deleted `src/Olive/Domain.cs`. It is retired; the FND_02 tests replace it. Original remains in git history.
- **Microsoft.NET.Test.Sdk version used:** 18.0.0 (unchanged, no vulnerability reported at restore).
- **Lockfile portability:** on Windows `dotnet restore` writes lockfiles with CRLF; `.gitattributes` forces LF in the index, so git warns but content is stable. `dotnet restore --locked-mode` passes. Running a restore after a temporary ProjectReference change rewrote lockfiles; those were reverted before commit.
- **.gitattributes and checkout:** the working copy of `vocabulary.yaml` was CRLF (autocrlf) with hash `3addb71b...`; after adding `-text` it was deleted and re-checked out to get the LF bytes matching `60a44f00...`. No content edit was made. `VENDORED.md` in the working tree remains CRLF but is only compared case-insensitively by hash.
- **Olive.sln:** regenerated with `dotnet new sln --format sln` because `dotnet sln` on the hand-written file (double-backslash paths) produced a broken file. Solution GUIDs changed.
- Negative proof done: HttpClient in Olive.Domain failed with RS0030; adding Domain -> Contracts reference failed FND_01. Both reverted.

## Deviations from Plan
**[Rule 3 - Blocking] Worktree base mismatch:** HEAD was at 6a02eca, not the expected base bfc8dc4; reset to the expected base per the worktree check before any work.

Otherwise the plan was executed as written. D-04 verified: `contracts/schemas`, `contracts/examples`, `catalog` untouched.

## Known Stubs
None (AssemblyMarker classes and minimal Program.cs files are intentional placeholders replaced by 01-02 and 01-05).

## Self-Check: PASSED
