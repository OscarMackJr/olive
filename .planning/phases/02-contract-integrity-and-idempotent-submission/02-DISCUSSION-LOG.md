# Phase 2: Contract Integrity and Idempotent Submission - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-22
**Phase:** 02-contract-integrity-and-idempotent-submission
**Areas discussed:** JSON Schema validator, JCS number handling, Catalog endpoint shape, Contracts layout and vectors

---

## JSON Schema validator

| Option | Description | Selected |
|--------|-------------|----------|
| JsonSchema.Net | Runtime 2020-12 validator with format assertion; covers envelope and catalog parameter schemas; ~3 packages added to the allowlist | ✓ |
| Corvus.JsonSchema codegen | Build-time generated validators; catalog schemas would need build-time generation too | |
| Hand-written validators | No package; explicit checks plus a minimal schema interpreter | |

| Option | Description | Selected |
|--------|-------------|----------|
| Vetted subset | Startup requires additionalProperties:false; no remote $ref; no default; format allowlist | ✓ |
| Meta-schema only | Any schema valid against the 2020-12 meta-schema; offline resolver | |
| You decide | Planner sets keyword rules | |

| Option | Description | Selected |
|--------|-------------|----------|
| Reason + JSON Pointer | Stable reason code plus pointer and keyword of the first failure; never the value | ✓ |
| Static reason only | invalid_request, like Phase 1 | |
| Pointers for all errors | List every failing pointer | |

| Option | Description | Selected |
|--------|-------------|----------|
| Keep as I-JSON gate | Phase 1 checks become the Q1 pre-idempotency gate; schema validation separate | ✓ |
| Fold into schema | Express what's possible as schema; delete hand checks | |
| You decide | Planner chooses within Q1 ordering | |

**User's choice:** all recommended options.

---

## JCS number handling

| Option | Description | Selected |
|--------|-------------|----------|
| Reject lossy numbers | 400 when the double can't round-trip to the same value; exact spellings allowed and hash the same | ✓ |
| Accept, JCS rounds | Follow RFC 8785 literally; large values may collide | |
| Forbid non-integers | Integers within ±2^53 only | |

| Option | Description | Selected |
|--------|-------------|----------|
| JCS bytes + jsonb | Store canonical bytes, body_sha256 and jsonb | ✓ |
| Raw bytes + jsonb | Store exact wire bytes (bytea), body_sha256 and jsonb | |
| Hash + jsonb only | No extra copy | |

| Option | Description | Selected |
|--------|-------------|----------|
| Whole body; 409 | nono_session_id is hashed; a changed session id gets 409 | ✓ |
| Exclude nono_session_id | Cross-session retry replays; deviates from §15.3 wording | |

| Option | Description | Selected |
|--------|-------------|----------|
| Yes, store it now | Compute and store the four-key proposal_hash in Phase 2 | ✓ |
| Defer to Phase 3 | Phase 3 adds proposal_hash | |

**User's choice:** all recommended options.

---

## Catalog endpoint shape

| Option | Description | Selected |
|--------|-------------|----------|
| An action_type id | GET /v1/catalog/crm.case_note@1 returns one entry | |
| A catalog release | GET /v1/catalog/1 returns all entries in release 1 | ✓ |
| Both | List route plus per-action route | |

| Option | Description | Selected |
|--------|-------------|----------|
| Any active key | Introspection first; no principal-type restriction | ✓ |
| Allowlisted agents only | Same checks as POST | |
| Unauthenticated | Public read | |

| Option | Description | Selected |
|--------|-------------|----------|
| Manifest file | catalog/catalog.yaml declares catalog_version and lists entries; startup fails on mismatch | ✓ |
| Single implicit release | Only release 1, made of every file | |
| Directory per release | catalog/v1/, catalog/v2/ | |

| Option | Description | Selected |
|--------|-------------|----------|
| 404; entries with schemas | 404 for unknown; 200 with full entries | ✓ |
| 404; schemas only | 200 with action_id and parameters only | |

| Option | Description | Selected |
|--------|-------------|----------|
| Current release only | POST looks up in the highest catalog_version; older releases readable via GET | ✓ |
| Any declared release | Accept from any release | |
| You decide | Planner chooses | |

**User's choice:** the user chose "A catalog release" over the recommended action_type id; the follow-up questions about the manifest, response and lookup rule all took the recommended options.

---

## Contracts layout and vectors

| Option | Description | Selected |
|--------|-------------|----------|
| Move proposal + action-type | Move these two; queue-item and execution-record wait for Phases 5–6 | |
| Move all now | Relocate all four into versioned folders; queue and execution are placeholders | ✓ |
| Only proposal | Move only the proposal schemas | |

| Option | Description | Selected |
|--------|-------------|----------|
| Request + resource | proposal-request, proposal resource and submit-response schemas with examples | ✓ |
| Request only | Only the POST body schema now | |

| Option | Description | Selected |
|--------|-------------|----------|
| Paired files + manifest | contracts/jcs/v1/ with .input.json / .canonical.json pairs and a vectors.json manifest | ✓ |
| Single JSON file | One vectors.json array | |

| Option | Description | Selected |
|--------|-------------|----------|
| Yes, as reject cases | Ship negative cases marked expect: reject | ✓ |
| Positive vectors only | Reject rules stay Olive-local | |

**User's choice:** "Move all now" rather than the recommended partial move; the other questions took the recommended options.

---

## Claude's Discretion

- Migration 0004 column names and types, following the 0002/0003 grant pattern.
- The format allowlist and exact reason-code names.
- The content of the 409 conflict log line (no values, no key).
- Which project hosts the JCS code and validator wrapper.
- The 200 replay's headers and body.

## Deferred Ideas

None. OpenAPI and COMPATIBILITY.md were already scheduled for Phase 9.
