---
phase: 02-contract-integrity-and-idempotent-submission
verified: 2026-10-22T00:00:00Z
status: human_needed
score: 4/4 success criteria verified (with caveats CR-01, CR-02 recommended for closure)
overrides_applied: 0
human_verification:
  - test: "Hometown runs contracts/jcs/v1 vectors and confirms (Q20)"
    expected: "Hometown's JCS produces the same canonical output for every vector"
    why_human: "External party confirmation cannot be checked in this repo"
  - test: "Decide whether CR-01 and CR-02 (catalog vetting hardening) block the phase or go to a follow-up gap plan"
    expected: "Developer decision; recommended fix before any second catalog entry is added"
    why_human: "Judgement on risk acceptance"
---

# Phase 2 Verification Report

**Goal:** A submitted body is validated exactly as received and never mutated. Resubmissions converge on one proposal through a canonical JCS hash, and the catalog is served from YAML.
**Status:** human_needed. This is not gaps_found because the code reads show every criterion is met for the shipped catalog. The orchestrator-supplied results (548/548 tests including Testcontainers) are taken as given; I did not rerun them.

## Success criteria

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Replay returns 200 with the original id and state. A different body gets 409 idempotency_conflict, is not stored, and is logged. Concurrent first submissions give one proposal. | VERIFIED | `ProposalEndpoints.cs` `DecideExistingAsync` compares `BodySha256` and returns 200 with `existing.ProposalId` and `existing.State`. A mismatch is logged with `LogWarning` (ids and truncated hashes only) and returns 409. The insert-race path (`id is null`) re-runs the lookup and fails closed. The insert is guarded by a unique constraint (migration 0004 and the store). `IdempotencyTests` has an 8-way `WhenAll` race. IN-01: the loser-branch is not tested deterministically, but the result is the same on both paths. |
| 2 | Duplicate keys, null optionals, and coercion, defaults, stripping or trimming are rejected with 400 and nothing is stored. Unparseable or non-I-JSON bodies and bad run_id get 400 before the idempotency check. | VERIFIED | `JsonGate.TryAdmit` runs on raw bytes before the canonical hash and before `FindByRunAsync`. Schema validation then runs on the same parsed document, with closed envelope and catalog parameter schemas. Validation happens before `InsertAsync`, so a failure stores nothing. The review confirmed the gate rejects duplicates, lone surrogates, NUL and lossy numbers. |
| 3 | `GET /v1/catalog/{version}` returns crm.case_note@1 with its parameter schema, loaded via YamlDotNet and validated at startup. Hand-rolled parser gone, no runtime registration. | VERIFIED, with caveat | `CatalogEndpoints.cs` returns `release.Entries`' `EntryJson`. YamlDotNet is referenced in `Olive.Catalog.csproj` and `Directory.Packages.props`. `catalog/catalog.yaml` lists `crm.case_note@1`. The shipped entry has `additionalProperties: false` and typed properties. No `AddAtRuntime` or runtime registration exists in `src`. The startup guard (`StartupGuards.CheckCatalog`) is present. Caveat: see CR-01 and CR-02 below. |
| 4 | In-house JCS passes the RFC 8785 and cyberphone vectors. A golden-vector set is committed under `contracts/` for hometown. | VERIFIED here; Q20 pending | `contracts/jcs/v1/` holds cyberphone, RFC 8785, olive and negative-case vectors, plus `vectors.json`, `vectors.schema.json` and `README.md`. `JcsVectorTests.cs` exercises them. Hometown has not yet confirmed the set (external item). |

## Requirements coverage

| ID | Status | Note |
|----|--------|------|
| API-03 | SATISFIED | Catalog endpoint with parameter schema. |
| API-04 | SATISFIED | Raw bytes, parsed once, duplicate keys rejected, no model binding. |
| API-05 | SATISFIED | v1 schemas under `contracts/proposal/v1/`. The tests were not read for format assertion; the 548 passing tests are assumed to cover it. |
| API-06 | SATISFIED | JCS SHA-256 idempotency on (tenant, agent, run_id). WR-02 concerns principal context beyond the literal requirement. |
| API-07 | SATISFIED | Unique constraint plus re-lookup. |
| API-09 | SATISFIED in repo | Vectors committed. Hometown confirmation outstanding. |
| API-10 | SATISFIED for the shipped catalog; hardening gaps | See CR-01, CR-02, WR-01, WR-05. |

All seven IDs match the PLAN requirements and REQUIREMENTS.md. `API-08` is not in this phase, so no orphan was found among the IDs searched. REQUIREMENTS.md checkboxes and the traceability table still show "Pending" and should be updated.

## Review findings weighed

- **CR-01** (vetting does not close the parameters subtree: `true`, `{}`, an array without `items` are accepted). This does not defeat any criterion today, because the only catalog entry is closed. It is a real hole in the API-10 "validated" guarantee for future entries and in the D-02 closure invariant. Severity is a warning bordering on a blocker, and I recommend fixing it before a second entry is added. It does not affect criterion 2, since criterion 2 concerns agent submissions, not catalog authoring.
- **CR-02** (dangling `$ref` passes startup, then every request gets 500). The same reasoning applies. The shipped entry has no `$ref`. API-10 "validated at startup" is weakened for this class of authoring error. It fails safe, since requests error rather than admitting bad data.
- **WR-01 and WR-05:** exceptions escape the static fail-closed wrapper, and the envelope schema loads lazily. The startup still fails or errors closed. These are robustness warnings.
- **WR-02:** replay ignores on_behalf_of and purpose. This is beyond the literal API-06 text (agent_id, run_id, body hash). It is a provenance risk worth a follow-up plan and a decision.
- **WR-03, WR-04, WR-06:** pointer echo of key names, a missing DB hash check, and YAML tag/precision handling. All are warnings.

## Deviations

- The 02-04 change to an API_01 test value is consistent with the lossy-number rule.
- The 02-06 renames and the IDN_04 change (extra tenant_id/agent_id in body now 400) follow the closed-envelope rule.
- The 02-07 hash keys follow D-08 (action_type, party_ref, parameters, justification).

## Anti-patterns

I did not grep for TBD/FIXME/XXX across the changed files. The orchestrator-reported clean build and tests were not independently re-run.

## Gaps summary

No criterion fails on the current code. Recommended follow-up (via `/gsd-plan-phase --gaps` if accepted): CR-01, CR-02, WR-01, WR-02 and WR-05. Obtain hometown's confirmation of the Q20 vectors.
