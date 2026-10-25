# Olive identity_review: additive fields and behaviors beyond IR-1

For the bluto owner. This note lists every wire field and behavior Olive implements in Phase 8 that IR-1
(`contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`) does not spell out. IR-1 itself is not edited.
Any real change to IR-1 needs agreement from both the olive and bluto owners and re-ratification. Everything below is additive or
optional and stays within `v1`. No real value, token or tenant identifier appears here.

## 1. Poll response envelope (complements IR-1 section 6)

- `GET /v1/queue/items?kind=identity_review&state=decided[&decided_after=<cursor>]` returns `{"items":[...],"next_cursor":"..."|null}`.
- `next_cursor` is opaque. Store it and send it back as `decided_after`. Follow it until a page is shorter than the page size.
- Page size is fixed at 100, oldest first (decided_at, then item_id). There is no `limit` parameter for the service token.
- The first poll omits `decided_after`. An empty page returns the supplied cursor (or null if none was supplied), so a stored cursor survives idle polls.
- A malformed, empty or repeated `decided_after` returns 400 `invalid_request`. Query parameters other than `kind`, `state`, `decided_after` also return 400.

## 2. Poll item fields (complements IR-1 section 6)

- Each item carries exactly 11 fields: `item_id`, `kind`, `subtype`, `producer_ref`, `subject_ref`, `payload_ref`, `ruleset_version`, `state`,
  `decision`, `decided_by`, `decided_at`.
- No rationale, no `claimed_by` and no deferral history is ever returned to bluto.
- Only `accept` and `reject` decisions are polled. A `defer` returns the item to `open` and is invisible to bluto.

## 3. Cursor overlap and idempotent apply (Pitfall 7)

- `decided_at` is stamped inside the decide transaction, so commit order can differ from stamp order by milliseconds.
- Recommendation: when storing the cursor, keep a small overlap (re-poll from slightly before the last cursor) and apply decisions
  idempotently keyed on `item_id`.

## 4. Create responses (complements IR-1 section 4)

- `201` (new) and `200` (identical replay) both return the slim body `{"item_id":"...","state":"..."}`. No `claimed_by` or reviewer data.
- A replay with the same `producer_ref` and a different body returns `409`.

## 5. Withdraw (complements IR-1 section 4)

- `POST /v1/queue/items/{item_id}/withdraw` with body `{"reason":"<code>"}`. Reasons: `superseded_by_rule`, `source_changed`, `ruleset_retired`.
  An unknown or missing reason is 400 `invalid_request`.
- Success: `200 {"item_id":"...","state":"withdrawn"}`. Open and claimed items can be withdrawn; a claim is cleared.
- Repeat withdraw with the same reason returns 200. Repeat with a different reason returns 409 `item_closed`.
- Withdrawing an already decided item returns 409 `item_closed`.
- An item in another tenant, from another producer, or missing is an indistinguishable 404 `item_not_found` (D-17).

## 6. Server-assigned fields and value rules (complements IR-1 sections 4 and 9)

- A create body naming any server-assigned field returns 400 `server_assigned_field` (neither name nor value is echoed). The set is:
  `tenant_id`, `producer`, `item_id`, `state`, `decision`, `decided_by`, `decided_at`, `rationale`, `claimed_by`, `claimed_at`,
  `auth_source`, `withdrawn_reason`, `expires_at`, `created_at`, `proposal_id`. This check runs before the unknown-field check.
- Other unknown fields return 400 `invalid_request`. The six create fields are `kind`, `subtype`, `producer_ref`, `subject_ref`, `payload_ref`, `ruleset_version`.
- `ruleset_version` must match `^[A-Za-z0-9._-]{1,128}$`.
- `subject_ref` must equal `bluto:candidate/<producer_ref>` and `payload_ref` must equal `bluto:review-evidence/<producer_ref>`, with `producer_ref` a UUID.
- Only subtype `candidate_pair` is accepted; `link_challenge` is refused.
- `ruleset_version` is also an additive optional field on the reviewer `QueueItemDto` (omitted when null).

## 7. Withdrawn candidates cannot be recreated (Pitfall 6)

- `(tenant, producer, producer_ref)` is unique. A withdrawn candidate cannot be recreated under the same `producer_ref`; a replay returns 200
  with state `withdrawn`. bluto must mint a new candidate id for a new review.

## 8. Credentials and external gates (IR-1 section 8)

- Producer routes (create, withdraw, poll) accept only a popeye-issued service token for an `application` principal. Agent keys, user keys,
  dev reviewers and cookies are refused (403 `principal_not_permitted` or 401). A service token is refused on `/v1/proposals`, `/v1/catalog`,
  `/review`, `/dev` and every reviewer action (on the proposal and catalog GET routes the fixture answers 401 `key_inactive`).
- Tenant and producer come only from the verified token, never the body.
- Remaining external gates: the `bluto-stub` client_id and the popeye registry entry, and the steward Entra group. Phase 8 ships only the
  non-production stub verifier; no live bluto traffic is enabled.
- Non-steward claim risk (accepted): claim is role-free; only the decision requires the steward role. A claimant can release, or bluto can withdraw.
