# Queue Item Contract IR-1: Identity Review (bluto → olive shared queue)

Version: 0.1
Status: **Ratified 2026-10-21** (record at the end). Approves the v1.1 integration shape. It does **not** authorise live bluto–olive traffic: that waits on the gates in §7 and §8.
Repository: `olive/contracts/queue/identity-review/` (olive owns the queue, so olive owns this contract). `bluto` cites it from `BLUTO_IDENTITY_SPINE_v0.1.1` §9.
Authority: program charter §6.3 (one shared review queue, in olive); `OLIVE_CHARTER_v0.4` §4 and REQ-QUE-01..04; `DECISION_olive-poc2-phase1_v0.5.md` §10–§13 (persistence and isolation) and §15 (idempotency and canonical form); `BLUTO_IDENTITY_SPINE_v0.1.1` §6, §7, §9.
Primary post-read action: olive and bluto owners agree §2–§6; then ratify. olive builds the queue generically in Phase 1. This contract registers one more `kind` on it.

**Revision 2026-10-21 (after owner review, still proposed):**
- **olive:** `tenant_id` must be **absent** from the create body, and olive rejects a body that supplies it. The same rule now covers `producer`. Silently ignoring a field would make the canonical body used for idempotency differ from what was sent.
- **bluto:** bluto vendors this contract with a pinned hash, so it can verify the exact text it implements (§10). v1 does not submit items (§4).
- **Both:** the popeye token path is stated as stub-only until popeye ratifies and implements the bluto registry capability (§8).

---

## 1. What This Is

olive's queue is built once, for three consumers: olive's own action approvals, bluto's identity review cases and evaluation flags. This contract defines the second: how bluto puts an identity-review case on the queue, what a reviewer decides, and how bluto learns the decision.

It is the **only** definition of this item. bluto's spec cites it and does not restate it.

**Division of responsibility:**

| | olive | bluto |
|---|---|---|
| Stores the item, its state and its decision history | **Yes**: the system of record for the review | No; it keeps only the `item_id` on the resulting link |
| Decides what is put on the queue | No | **Yes**: anything its deterministic rules do not auto-link (spec §4) |
| Shows the item to a reviewer | **Yes**: the queue UI (list, claim, decide, audit) | No |
| Holds the evidence the reviewer looks at | No: references only (REQ-QUE-01) | Holds the candidate and the rule evidence; never business attributes |
| Applies the decision to identity data | No | **Yes**: writes the link, or not |

## 2. Item Shape

olive's charter item shape, with `kind = identity_review`. Every field is an identifier, a reference, a state or audit metadata. **No names, addresses, identifiers or other record values appear in the item.**

| Field | Type | Set by | Value |
|---|---|---|---|
| `item_id` | uuid | olive | Minted on creation |
| `kind` | string | bluto | `identity_review` |
| `subtype` | string | bluto | `candidate_pair` (v1.1). `link_challenge` is **reserved** for splits (§7); olive rejects it until it is added |
| `tenant_id` | uuid | olive, from the caller's credential | One vocabulary tenant UUID; never `null`, never a display label. **Must be absent from the create body** (§4) |
| `purpose` | — | — | Absent. Identity review is not a purpose-scoped data use (§7, open item 2) |
| `producer` | string | olive, from the caller's credential | `bluto`. **Must be absent from the create body** (§4) |
| `producer_ref` | uuid | bluto | bluto's `candidate_id`. With `producer` and `tenant_id`, the idempotency key (§4) |
| `subject_ref` | string | bluto | `bluto:candidate/<candidate_id>` |
| `payload_ref` | string | bluto | `bluto:review-evidence/<candidate_id>`, resolved by the reviewer's client under the reviewer's own credential (§5) |
| `ruleset_version` | string | bluto | The identity ruleset version that produced the candidate (git SHA) |
| `state` | enum | olive | `open` → `claimed` → `decided`; or `withdrawn` (§4) |
| `claimed_by`, `decided_by` | string | olive | The authenticated Entra identity of the reviewer |
| `decision` | enum | olive, from the reviewer | `accept` \| `reject` \| `defer` (§3) |
| `decided_at` | timestamptz | olive | |
| `rationale` | string | the reviewer | Required on every decision (REQ-QUE-03). Must not contain record values; the UI says so |
| `created_at` | timestamptz | olive | |

`payload_ref` is a reference, never a body. olive rejects an item carrying any inline payload (REQ-QUE-01, TC-QUE-01).

## 3. Decisions

| Decision | Meaning | bluto's action |
|---|---|---|
| `accept` | The two source records are the same entity | Write an `active` link with `match_method = review`, `asserted_by` = `decided_by`, `review_item_id` = `item_id` (spec §3.2, §9) |
| `reject` | They are not the same entity | No link. Record the rejection against the candidate, so the same pair is not re-queued under the same ruleset version |
| `defer` | The reviewer can't decide yet | No link. olive returns the item to `open`, keeping the defer in its history. bluto does nothing |

- **Who may decide:** members of the Entra group behind `role:identity_steward`, checked at decision time (the same rule as olive record §8). The group is bluto's open item "data stewardship ownership" and must be named before v1.1.
- **No inline edits.** A reviewer can't change the candidate; they decide on it. A different pairing is a new candidate from bluto.

## 4. Creating And Withdrawing Items (bluto → olive)

```
POST /v1/queue/items          create
POST /v1/queue/items/{item_id}/withdraw   withdraw an undecided item, with a reason code
```

- **Credential.** bluto calls as a registered application (`principal_type = application`) with a **tenant-scoped service token from popeye**, the same mechanism olive uses to call hometown (olive record §17.1), but with **no delegated agent**.
- **Fields olive sets are not accepted in the body.** The create body carries exactly `kind`, `subtype`, `producer_ref`, `subject_ref`, `payload_ref` and `ruleset_version`. A body that also supplies `tenant_id`, `producer`, `item_id`, `state` or any decision field is rejected with `400 server_assigned_field`, naming no value. olive never ignores such a field: the canonical body it hashes for idempotency must be exactly what bluto sent. Unknown fields are rejected as `400 invalid_request`.
- **Tenant.** Always the popeye token's `tenant_id`. This adds a bluto entry to popeye's service registry: a second consumer of the dependency popeye acknowledged on 2026-10-20 (§8).
- **Idempotency.** Keyed on `(producer, tenant_id, producer_ref)`, with the rules of olive record §15.2–§15.3. An identical body (RFC 8785 canonical form) returns the existing `item_id` and current state. A different body under the same key returns `409 idempotency_conflict`. A re-run of bluto's batch job therefore never duplicates an item.
- **No policy check before queueing for this kind.** olive's rule "policy check precedes human review" is about AI action proposals. For identity items **bluto's deterministic threshold is the pre-queue gate**: only what the rules couldn't settle reaches a person. olive validates structure only: kind, subtype, references, tenant.
- **v1 sends nothing.** bluto v1 records unresolved candidates locally and submits none (bluto spec §3.3, §12). Submission, withdrawal and decision polling start in v1.1.
- **Withdrawal.** bluto withdraws an undecided item when a later run makes the candidate obsolete, for example because a source key changed or a deterministic rule now links the pair. Reason codes: `superseded_by_rule`, `source_changed`, `ruleset_retired`. A decided item can't be withdrawn.
- **No expiry.** Unlike action proposals (olive record §9), identity items don't expire: a correct identity decision doesn't go stale on a timer. olive surfaces item age in the queue list, and bluto withdraws obsolete items itself.

## 5. Reviewer Evidence (`payload_ref`)

- The reviewer's client resolves `bluto:review-evidence/<candidate_id>` through **bluto's** evidence endpoint, with the reviewer's own credential. olive never resolves it and never stores what comes back.
- bluto returns what it holds: the two `(source_system, source_key)` pairs, the rule ids that considered them, and why no deterministic rule settled them. **No business attributes and no strong identifiers**, raw or tokenised (bluto spec §5, §10).
- To see names, dates or addresses, the reviewer's client reads them from the source systems through hometown's governed path, under the reviewer's own entitlements. Which purpose governs that read is open (§7, item 2).

## 6. Learning Decisions (olive → bluto)

```
GET /v1/queue/items?kind=identity_review&state=decided&decided_after=<cursor>
```

- **Polling, tenant by tenant.** bluto's pipeline is batch (spec §8), so it pulls decisions at the start of each run, once per tenant token. No callback; olive never calls bluto.
- **Applying is idempotent on `item_id`.** Applying the same decision twice produces one link, because the link carries `review_item_id`.
- **Stale decisions aren't applied.** If the candidate was superseded after the reviewer decided (a merge, a new deterministic link, a source change), bluto records the decision against the candidate but doesn't apply it, and logs `review.decision_stale` with the `item_id`. Identity truth comes from bluto's effective-dated links, not from the queue.
- The cursor is olive's `decided_at` plus `item_id`, so ties are ordered. bluto stores its cursor per tenant.

## 7. Open Items

1. **`link_challenge` (splits).** bluto spec §7 defines split ("a bad match undone"), but not who triggers it. Proposed: a steward raises a `link_challenge` on an existing link, with decisions `uphold | split | defer`. This is **reserved** until the merge/split semantics are approved (bluto open decision 1).
2. **Purpose for the reviewer's evidence reads.** The reviewer must see source attributes to decide, and hometown requires a purpose for every read. The seven vocabulary purposes may not include one that fits identity stewardship. Either map it to an existing purpose or propose an addition to the vocabulary, which is a program-level contract change.
3. **Records with no tenant.** olive requires a tenant on every item. bluto's sources include systems that may not be tenant-scoped (the CRM, the ledger). Decide whether untenanted records belong to a designated enterprise tenant UUID in the vocabulary, or are outside the queue.
4. **Authorised cross-tenant resolution** (bluto spec §6) produces candidates spanning two tenants. Out of scope for v1.1's queue: such candidates are not queued until the scoping is designed.

## 8. Dependencies

| Dependency | Owner | Status | Until then |
|---|---|---|---|
| olive's generic queue (list, claim, decide, audit; Postgres with RLS) | olive | In olive Phase 1 build | bluto tests against an olive queue stub |
| Tenant-scoped service tokens, **with a `bluto` registry entry and no delegated agent** | popeye | Dependency acknowledged 2026-10-20 for olive only; **bluto entry not yet raised** | **Stub-only.** No live olive–bluto traffic until popeye separately ratifies and implements the bluto registry capability |
| `role:identity_steward` Entra group | bluto (business owner) | Not named | No real decisions |
| bluto's evidence endpoint | bluto | v1.1 | — |

## 9. Tests

| TC | Test | Pass criteria |
|---|---|---|
| TC-IRQ-01 | Create with an inline payload, or any field holding a record value | Rejected (with TC-QUE-01) |
| TC-IRQ-02 | Create twice with an identical body; then with a different body under the same `producer_ref` | One item; second call returns it. Different body → `409 idempotency_conflict` |
| TC-IRQ-03 | Create with a token for tenant A and **any** `tenant_id` in the body (B, or A itself); separately with `producer` in the body | Each rejected with `400 server_assigned_field`; no item created. The same body without those fields creates a tenant A item |
| TC-IRQ-04 | Reviewer outside `role:identity_steward` decides | Refused |
| TC-IRQ-05 | Decide without `rationale` | Rejected (with TC-QUE-03) |
| TC-IRQ-06 | `defer`, then `accept` | History shows both; state returns to `open` after defer; bluto applies only the `accept` |
| TC-IRQ-07 | bluto applies the same decided item twice | One link, `review_item_id` = `item_id` |
| TC-IRQ-08 | Candidate superseded after the decision | Decision recorded, not applied; `review.decision_stale` logged |
| TC-IRQ-09 | Withdraw an undecided item; withdraw a decided item | First succeeds with a reason code; second refused |
| TC-IRQ-10 | Poll with a tenant A token | Only tenant A items (with TC-QUE-02, both isolation layers) |
| TC-IRQ-11 | `subtype: link_challenge` | Rejected until §7 item 1 is decided |

---

## 10. Distribution

olive's copy, at `olive/contracts/queue/identity-review/`, is authoritative. bluto vendors it at `bluto/contracts/vendored/olive/QUEUE_ITEM_CONTRACT_identity-review_v<version>.md` with its SHA-256 recorded in `VENDORED.md`, the same practice as the shared vocabulary. bluto's CI fails if the vendored file's hash differs from the pinned value, so bluto always knows exactly which text it implements. A new IR-1 version is re-vendored deliberately, never fetched at build time.

---

## Ratification

| Field | Value |
|---|---|
| Decision | **Ratified**: IR-1 v0.1 as revised 2026-10-21 (server-assigned fields forbidden in the create body; stub-only popeye path; vendoring) |
| olive owner agreement | **Approved 2026-10-21**. The tenant and idempotency ambiguity is resolved; the popeye path is stub-only |
| bluto owner agreement | **Approved 2026-10-21** after the cross-contract review against `BLUTO_IDENTITY_SPINE_v0.1.1`. olive owns queue state and decisions; bluto owns candidates, evidence and applying valid decisions |
| Ratified by | Oscar Mack (architecture and risk decisions) |
| Ratified together with | `BLUTO_IDENTITY_SPINE_v0.1.1.md` |
| Date | 2026-10-21 |
| Not decided here | The four §7 open items; popeye's registry entry for bluto |
| Live-operation gates (unchanged) | popeye specifies, ratifies and implements the bluto registry entry (tenant tokens, no delegated agent); the Entra group for `role:identity_steward` is named; §7 items 2 and 3 are answered before real review items are created. Until then, stubs only |
| Distribution | This ratified file supersedes the copy vendored for review. bluto re-vendors it once and updates `VENDORED.md` with its new SHA-256; olive's copy is authoritative |
| Change control | Changes need both owners' agreement and re-ratification. Additive optional fields need both owners' agreement only |
