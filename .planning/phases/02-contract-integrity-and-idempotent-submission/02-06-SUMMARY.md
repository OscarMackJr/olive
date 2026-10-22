---
phase: 02-contract-integrity-and-idempotent-submission
plan: 06
subsystem: api
tags: [json-schema, validation, catalog, problem-details]
requires:
  - phase: 02-02
    provides: SchemaValidator and v1 contracts
  - phase: 02-04
    provides: JsonGate.TryAdmit in ProposalEndpoints
  - phase: 02-05
    provides: CatalogSet singleton and CatalogLoader
provides:
  - SubmissionValidator (envelope, current-release lookup, parameter schema)
  - schema_violation and not_in_catalog problem reasons with pointer and keyword extensions
affects: [02-07, phase-04]
tech-stack:
  added: []
  patterns: ["value-free first-error reporting (pointer and keyword only)", "single LookupAction seam for Phase 4"]
key-files:
  created: [src/Olive.Catalog/SubmissionValidator.cs, tests/Olive.Tests/SubmissionValidatorTests.cs]
  modified: [src/Olive.Contracts/ProblemReasons.cs, src/Olive.Host/Problems/ProblemResults.cs, src/Olive.Host/Endpoints/ProposalEndpoints.cs, src/Olive.Host/Program.cs, tests/Olive.Tests/ApiIntegrityTests.cs, tests/Olive.Tests/ApiProposalTests.cs, tests/Olive.Tests/IdentityEndpointTests.cs]
key-decisions:
  - "Unknown action_type is a non-stored 400 not_in_catalog behind SubmissionValidator.LookupAction (Phase 4 replaces it)"
  - "Hostile-parameter 201 tests use a test-only permissive CatalogSet registered via configureServices"
requirements-completed: [API-04, API-05, API-10]
duration: 25min
completed: 2026-10-22
---

# Phase 2 Plan 06: Submission validation Summary

Every POST /v1/proposals is now validated as received against the v1 envelope schema, the current catalog release and the catalog parameter schema, and failures are value-free 400s that store nothing.

## Tasks

1. SubmissionValidator and problem reasons: commit caad8d4.
2. Endpoint wiring, Phase 1 fixture migration, integration tests: commit 81905c0.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Hostile-parameter success tests cannot satisfy the closed crm.case_note@1 schema**
- Issue: NumBody tests (numbers, emoji, escaped NUL in parameters) need 201, but the real parameter schema only allows case_ref and note_ref strings.
- Fix: added `ApiProposalTests.PermissiveCatalog()` (test.open@1 and crm.case_note@1 accept any object), registered through `configureServices` in those tests only. Assertions unchanged.

**2. [Rule 1 - Superseded test] `API_01_only_run_id_is_mandatory` contradicts the user-decided all-required envelope**
- Renamed and inverted to `API_04_run_id_alone_is_not_enough_every_envelope_field_is_required` (expects 400 schema_violation). This is the only Phase 1 method not kept by name.

**3. [Rule 1] `IDN_04_body_tenant_is_ignored`**: extra `tenant_id`/`agent_id` fields are now refused by the closed envelope. Test now asserts that refusal (nothing stored) and keeps the original proof with a Beta GUID in the allowed `nono_session_id` field.

Also: the API_01 invalid-body theory was split as planned (gate cases stay `invalid_request`; schema cases moved to the new API_05 theory, plus the former "unknown field" case). `BodyOfSize` now builds a full envelope.

## Verification

Full `dotnet test tests/Olive.Tests` (with Docker): 532 passed, 0 failed.

## Environment note

With core.autocrlf=true the checkout left `contracts/vendored/vocabulary.yaml` with CRLF, breaking the vendored hash guard. It was rewritten locally from the git blob (LF) to run tests; that file shows as modified in the worktree only because of line endings and was NOT committed.

## Known Stubs

None.

## Self-Check: PASSED
