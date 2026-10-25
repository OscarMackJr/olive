# queue-item v1 (IR-1 item)

Review queue item shared by `action_approval` (producer `olive`) and `identity_review` (producer `bluto`). Supersedes the v0 placeholder (D-13). `additionalProperties` is false.

## Required fields

`item_id`, `kind`, `subtype`, `tenant_id`, `producer`, `producer_ref`, `subject_ref`, `payload_ref`, `state`, `created_at`.

## References only (QUE-01)

`subject_ref` and `payload_ref` are anchored, fixed-shape references: `olive:proposal/<uuid>`, `bluto:candidate/<uuid>` or `bluto:review-evidence/<uuid>`. No inline body, parameter or free text is allowed. For `action_approval` both refs equal `olive:proposal/<proposal_id>` and `producer_ref` is the proposal id; the database enforces the same rule.

## Vocabularies

| kind | subtype | decisions |
|------|---------|-----------|
| action_approval | proposal | approve, reject |
| identity_review | candidate_pair | accept, reject, defer |

States: `open`, `claimed`, `decided`, `withdrawn`. A decided item carries `decided_by` equal to `claimed_by`, a non-blank `rationale`, a `decision` and an `auth_source` (`entra` or `stub`). A withdrawn item carries `withdrawn_reason` (for example `proposal_expired`).

Optional: `ruleset_version`, `claimed_by`, `claimed_at`, `decision`, `decided_by`, `decided_at`, `rationale`, `auth_source`, `withdrawn_reason`, `expires_at`, `age_seconds`.

Examples: `queue-item.valid.json`, `queue-item.invalid.json` (unknown property), `queue-item.inline-payload.invalid.json` (inline body in `payload_ref`).

## identity-create (producer create body)

`identity-create.schema.json` is the body bluto POSTs to `/v1/queue/items`: exactly `kind`, `subtype`, `producer_ref`, `subject_ref`, `payload_ref`, `ruleset_version`, `additionalProperties` false. `kind` is `identity_review`, `subtype` is `candidate_pair` only (`link_challenge` is rejected), the refs are `bluto:candidate/<producer_ref>` and `bluto:review-evidence/<producer_ref>`, and `ruleset_version` matches `^[A-Za-z0-9._-]{1,128}$` so no record value can ride in it. Tenant, producer and every decision or server field come from the verified token and `producers.yaml`, never the body.

Examples: `identity-create.valid.json`, `identity-create.invalid.json` (inline `payload`), `identity-create.link-challenge.invalid.json`, `identity-create.server-assigned.invalid.json` (`tenant_id` in the body).
