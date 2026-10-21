---
phase: 01-foundation-and-walking-skeleton
plan: 03
subsystem: identity-primitives
tags: [secrets, redaction, introspection, popeye-stub, allowlist, yamldotnet]
requires: [01-01]
provides:
  - SessionKey and ServiceToken redacting secret types
  - IKeyIntrospector port, IntrospectionResult, StubKeyIntrospector with six schema-checked fixtures
  - AgentKeyHeader (single owner of the header name)
  - ProposalStates and PrincipalTypes constants
  - AgentAllowlist from embedded config/agents.yaml
affects: [01-05, 01-06, 01-07]
tech-stack:
  added: []
  patterns: [sealed non-record secret classes, fixture-driven stub marked INonProductionOnly, fail-closed strict YAML]
key-files:
  created:
    - src/Olive.Domain/SessionKey.cs
    - src/Olive.Domain/ServiceToken.cs
    - src/Olive.Domain/RedactingJsonConverter.cs
    - src/Olive.Domain/ProposalStates.cs
    - src/Olive.Domain/PrincipalTypes.cs
    - src/Olive.Application/AgentKeyHeader.cs
    - src/Olive.Application/IKeyIntrospector.cs
    - src/Olive.Application/IntrospectionResult.cs
    - src/Olive.Application/IntrospectionUnavailableException.cs
    - src/Olive.Application/INonProductionOnly.cs
    - src/Olive.Application/IAgentAllowlist.cs
    - src/Olive.Integrations.Popeye/StubKeyIntrospector.cs
    - src/Olive.Catalog/AgentAllowlist.cs
    - config/agents.yaml
    - contracts/introspection/ (schema, index, six fixtures, README)
    - tests/Olive.Tests/ (SecretTypeTests, AgentKeyHeaderTests, JsonSchemaLite, IntrospectionFixtureTests, StubKeyIntrospectorTests, AgentAllowlistTests, SourceHygieneTests)
  modified: []
key-decisions:
  - "D-12: Authorization: Bearer header isolated in AgentKeyHeader; assumption flagged for the nono owner"
  - "D-14: no introspection cache in Phase 1; POPEYE 2.2 ceiling recorded in IKeyIntrospector comment"
  - "D-15: allowlist is an embedded source-controlled file, no env/IConfiguration path"
requirements-completed: [FND-06, IDN-01, IDN-02]
duration: 35min
completed: 2026-10-21
---

# Phase 1 Plan 03: Identity Building Blocks Summary

Self-redacting SessionKey/ServiceToken, a fixture-driven fail-closed popeye introspection stub marked INonProductionOnly, a single-owner Authorization header reader, and a source-controlled liam allowlist; 75 tests green.

## Commits
- a3e947d feat: redacting secret types, introspection port, agent-key header reader
- 9d0876c feat: introspection fixtures, schema and fixture-driven popeye stub
- (task 3) feat: source-controlled agent allowlist and source-hygiene tests

## Notes
- **D section 4 stop rule:** `~/fiskroad/specs/POPEYE_v0.4.2.md` was present at run start.
- **Source-spec divergence (flag for Phase 3 popeye research):** POPEYE_v0.4.2 section 2.1 says a non-agent key returns only `active: false`, but D section 7 and Phase 1 success criterion 1 require `403 principal_not_permitted` for user and application keys. `user.json` and `application.json` therefore return `active: true` with the matching `principal_type` so Olive's own principal check is exercised. Documented in `contracts/introspection/README.md` and the stub header comment.
- **Open item for nono owner (written to STATE.md by 01-07):** the `Authorization: Bearer` header name is an assumption (D-12).
- Embedded resource globs declared in 01-01 worked unchanged; no csproj, package, or lockfile changes (no collision with 01-02).
- FND_05 negative proof done: a `Stub*` class without the marker in Olive.Host failed the test; reverted.
- `contracts/vendored`, `contracts/schemas`, `contracts/examples` untouched. `contracts/vendored/VENDORED.md` shows a working-tree modification (line endings from the base reset); deliberately not committed.
- TDD: tests and implementation were committed together per task (no separate RED commits).

## Deviations from Plan
**[Rule 3 - Blocking] Worktree base mismatch:** HEAD was at 6a02eca, not f835f42; reset to the expected base before any work, vocabulary.yaml re-checked out (sha256 60a44f00).

Otherwise the plan was executed as written. One extra test was added beyond the plan (`IDN_02_yamldotnet_confined_to_catalog_and_header_name_has_one_owner`).

## Known Stubs
`StubKeyIntrospector` is an intentional, INonProductionOnly stub; production guard lands in 01-06.

## Self-Check: PASSED
Files and commits verified present; `dotnet test` (75 passed) and `dotnet build Olive.sln -c Release` (0 warnings, 0 errors) green.
