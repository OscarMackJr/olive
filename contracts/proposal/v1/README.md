# contracts/proposal/v1

JSON Schema draft 2020-12, format assertion on. Each schema has `<name>.valid.json` and `<name>.invalid.json` beside it.

- `proposal-request.schema.json`: POST /v1/proposals body envelope.
- `proposal.schema.json`: GET /v1/proposals/{id} resource.
- `proposal-submit-response.schema.json`: 201 / 200 reply.

## Additive changes (Phase 4, policy and expiry)

All within `v1`; every change is additive and optional.

- Refusal reasons: `not_in_catalog`, `purpose_not_permitted`, `entitlement_missing`, `rate_limited` now appear in `refusal_reason` alongside the `trace_*` reasons. The submit-response schema constrains the field by pattern, not enum, so no schema change was needed. Examples: `*.policy-refused.valid.json`.
- Optional request field `expires_in_seconds` (integer, minimum 1, at most the catalog entry's maximum). It is excluded from `proposal_hash` and included in `body_sha256`.
- Proposal field `expires_at` (date-time) on accepted proposals, capped at the key's expiry.
- Accepted submissions return state `awaiting_approval`; policy refusals return `refused_policy` with 201, and a replay returns 200 with the same body.
- New problem reason `directory_unavailable` (HTTP 503) when the entitlement lookup cannot be completed; nothing is stored.

Envelope decision: `run_id`, `action_type`, `party_ref`, `parameters`, `justification`, `trace_ref` are required; `nono_session_id` is optional. The schema is closed (`additionalProperties: false`) and no field accepts `null`.
