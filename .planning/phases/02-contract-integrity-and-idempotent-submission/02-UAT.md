---
status: complete
phase: 02-contract-integrity-and-idempotent-submission
source: [02-01-SUMMARY.md, 02-02-SUMMARY.md, 02-03-SUMMARY.md, 02-04-SUMMARY.md, 02-05-SUMMARY.md, 02-06-SUMMARY.md, 02-07-SUMMARY.md]
started: 2026-10-22T15:00:00Z
updated: 2026-10-22T16:38:31Z
---

## Current Test

[testing complete]

## Tests

### 1. Cold Start Smoke Test
expected: Stop any running host. Migrator applies 0004_proposal_body_hash cleanly (OLIVE_MIGRATOR_URL in shell only). Host then starts with no startup-guard failure, and GET /v1/catalog/1 with an active agent key returns 200.
result: pass

### 2. Catalog served from YAML
expected: GET /v1/catalog/1 returns the crm.case_note@1 entry with its parameter JSON Schema (case_ref and note_ref, additionalProperties false). GET /v1/catalog/999 returns a not-found problem. No endpoint or API exists to register an action type at runtime.
result: pass

### 3. Idempotent replay of an identical body
expected: POST /v1/proposals with the body from contracts/proposal/v1/proposal-request.valid.json (fresh run_id) returns 201 with proposal_id and state. Re-POSTing the byte-identical body (or the same JSON with keys reordered or whitespace changed) under the same run_id returns 200 with the same proposal_id, the current state and the same Location header. Exactly one proposal row exists for that run_id.
result: pass

### 4. Different body under the same run_id is refused
expected: Re-POSTing under the same run_id with any changed field (e.g. justification) returns 409 with reason idempotency_conflict. No new row is stored, the original proposal is unchanged, and the host log has a 409 entry carrying only tenant_id, agent_id, proposal_id and 12-char hash prefixes (no body values).
result: pass

### 5. Strict I-JSON gate rejects bad bodies before anything is stored
expected: Each of these returns 400 invalid_request and stores nothing: a body with a duplicate key (e.g. two "justification" keys), a lone surrogate escape ("\ud800"), a lossy number such as 9007199254740993 anywhere in the body, unparseable JSON, and a missing or invalid run_id. The run_id checks fire before the idempotency lookup (a bad body reusing an existing run_id still gets 400, not 200/409).
result: pass

### 6. Schema validation is exact and value-free
expected: Each returns 400 and stores nothing: a null optional field, an extra envelope field (e.g. tenant_id), a parameter of the wrong type or an extra parameter → schema_violation with only a JSON pointer and keyword in the problem body (no submitted values echoed); an unknown action_type (e.g. crm.nope@1) → not_in_catalog. No value is coerced, defaulted, stripped or trimmed into validity (e.g. " crm.case_note@1" with a leading space is rejected).
result: pass

### 7. Broken catalog fails closed at startup
expected: Temporarily break the catalog (e.g. add a duplicate key to catalog/crm.case_note@1.yaml, or remove additionalProperties:false from its parameters, or list an entry in catalog.yaml with no file). The host refuses to start with a StartupGuard catalog error instead of serving requests. Reverting the change lets it start again.
result: pass

### 8. JCS golden vectors and test suite
expected: contracts/jcs/v1 contains README.md, vectors.json and vectors.schema.json. `dotnet test tests/Olive.Tests` (Docker running) passes in full, including JcsVectorTests, JsonGateTests and the IdempotencyTests concurrent-first-submission test (two parallel first submissions → exactly one proposal).
result: pass

## Summary

total: 8
passed: 8
issues: 0
pending: 0
skipped: 0

## Gaps

[none yet]
