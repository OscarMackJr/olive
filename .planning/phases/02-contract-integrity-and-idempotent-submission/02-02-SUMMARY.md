---
phase: 02-contract-integrity-and-idempotent-submission
plan: 02
subsystem: contracts
tags: [json-schema, jsonschema-net, contracts, validation]
requires: []
provides:
  - SchemaValidator (Compile/Validate returning first pointer + keyword only)
  - Versioned contracts/<name>/v<N>/ layout with v1 proposal schemas
affects: [02-05, 02-06]
tech-stack:
  added: [JsonSchema.Net 8.0.5, JsonPointer.Net 6.0.1, Json.More.Net 2.2.0]
  patterns: [dialect forced to 2020-12 in memory, private SchemaRegistry per compile, embedded schemas]
key-files:
  created:
    - src/Olive.Catalog/SchemaValidator.cs
    - src/Olive.Catalog/SchemaViolation.cs
    - contracts/proposal/v1/ (3 schemas, 6 examples, README)
    - tests/Olive.Tests/SchemaValidatorTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
  modified:
    - Directory.Packages.props
    - src/Olive.Catalog/Olive.Catalog.csproj
    - tests/Olive.Tests/ArchitectureTests.cs
    - tests/Olive.Tests/IntrospectionFixtureTests.cs
    - CLAUDE.md
  deleted: [tests/Olive.Tests/JsonSchemaLite.cs, contracts/schemas/, contracts/examples/]
key-decisions:
  - "JsonSchema.Net pinned 8.0.5 (MIT), not 9.x (OSMF EULA)"
  - "Each Compile uses its own SchemaRegistry so repeated $id compiles do not collide and no Fetch is registered"
requirements-completed: [API-05]
duration: ~25m
completed: 2026-10-22
---

# Phase 2 Plan 02: JSON Schema validator and versioned contracts Summary

JsonSchema.Net 8.0.5 wrapped in an offline, 2020-12, format-asserting `SchemaValidator` that returns only (pointer, keyword), plus all contracts moved into versioned folders with the three new v1 proposal schemas and paired examples.

## Tasks

| Task | Name | Commit |
|------|------|--------|
| 1 | Package legitimacy checkpoint | approved by user (no files changed) |
| 2 | JsonSchema.Net, SchemaValidator, allowlist, lockfiles | ee52ad9 |
| 3 | Versioned contracts, v1 schemas, real-validator tests, doc paths | 97cc4ff |

## Verification

- `dotnet restore --locked-mode` passes; `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages for all 12 projects.
- `dotnet test tests/Olive.Tests --filter "Category!=Integration"`: 160 passed, 0 failed.
- Integration tests (Docker/Testcontainers) were not run.

## Deviations from Plan

**1. [Rule 1 - Bug] Duplicate `$id` registration**
- **Found during:** Task 3 (compiling the same schema file repeatedly in tests)
- **Issue:** JsonSchema.Net's global registry threw "Overwriting registered schemas is not permitted".
- **Fix:** `Compile` passes `new BuildOptions { SchemaRegistry = new SchemaRegistry() }`; no Fetch delegate is registered.
- **Files:** src/Olive.Catalog/SchemaValidator.cs (in Task 3 commit)

**2. TDD ordering (Task 2):** tests and implementation were committed together as a single feat commit instead of separate RED/GREEN commits, since tests cannot compile without the type.

**3. Dialect forcing:** `$schema` is always overwritten with 2020-12 on an in-memory copy (simplest form of the plan's rule).

## Notes

- Lockfile regeneration with `core.autocrlf=true` rewrote line endings in unrelated `packages.lock.json` files and CRLF-ified `contracts/vendored/*` in the working copy. Only the three lockfiles with real changes (Catalog, Host, Tests) were committed; vendored files were restored from HEAD and never committed. `git diff -- contracts/vendored` is empty.
- README.md, AGENTS.md and docs/STATUS.md had no references to the old paths; only CLAUDE.md changed.
- Remote `$ref` test asserts an exception (no network resolution).

## Known Stubs

None.

## Self-Check: PASSED
