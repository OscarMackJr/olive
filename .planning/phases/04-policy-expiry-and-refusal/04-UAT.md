---
status: complete
phase: 04-policy-expiry-and-refusal
source: [04-01-SUMMARY.md, 04-02-SUMMARY.md, 04-03-SUMMARY.md, 04-04-SUMMARY.md, 04-05-SUMMARY.md, 04-06-SUMMARY.md, 04-07-SUMMARY.md]
started: 2026-10-23T00:00:00Z
updated: 2026-10-23T16:18:14Z
---

## Current Test

[testing complete]

## Tests

### 1. Cold Start Smoke Test
expected: Stop any running host. Run the migrator (OLIVE_MIGRATOR_URL in the shell only); it applies 0007_policy_expiry_rate cleanly. The host then starts with no startup-guard failure, and GET /v1/catalog/1 with an active liam key returns 200.
result: pass

### 2. Catalog exposes the policy inputs
expected: GET /v1/catalog/1 shows crm.case_note@1 with required_entitlement "ent:crm.case_note.write" and max_expiry_seconds 28800, alongside required_purpose [servicing, collections] and max_rate (10 per 3600 s). Removing required_entitlement (or setting max_expiry_seconds to 0) in catalog/crm.case_note@1.yaml makes the host refuse to start; reverting lets it start.
result: pass

### 3. Accepted proposal lands in awaiting_approval with expires_at
expected: POST /v1/proposals with the valid body (fresh run_id, matching fixture trace) returns 201 with state awaiting_approval. GET /v1/proposals/{id} shows an absolute expires_at about 8 hours after creation. With "expires_in_seconds": 600 it is about 10 minutes out instead. Values 0, 28801, 1.0, 1e0 or a string each return 400 and store nothing.
result: pass

### 4. Unknown action is a stored refusal, not an error
expected: POST with action_type "crm.nope@1" (fresh run_id) returns 201 with state refused_policy and reason not_in_catalog — not a 400 or 500. No trace lookup happens for it. GET shows expires_at absent. The proposal_event rows are submitted, policy_evaluated and policy_refused, and the policy_evaluated detail holds only check names and reason codes (no parameter values).
result: pass

### 5. Wrong purpose is refused before any human sees it
expected: A proposal whose purpose is not in [servicing, collections] (fresh run_id, matching trace) returns 201 refused_policy with reason purpose_not_permitted. It never reaches awaiting_approval. A trace mismatch still wins over a policy refusal (trace reason, not purpose).
result: pass

### 6. Missing entitlement is refused
expected: Temporarily remove "ent:crm.case_note.write" from groups in contracts/directory/analyst-alpha.json (or from liam in config/agents.yaml), rebuild and restart. A valid alpha proposal returns 201 refused_policy with reason entitlement_missing. Neither the user's groups nor on_behalf_of appear in the event detail or the host log. Reverting restores 201 awaiting_approval.
result: pass

### 7. Rate limit refuses the 11th accepted proposal
expected: Within one hour, 10 accepted submissions for alpha/liam/crm.case_note@1 reach awaiting_approval; the 11th returns 201 refused_policy with reason rate_limited and no expires_at. Refused proposals do not count toward the 10. Tenant beta is unaffected by alpha's budget.
result: pass

### 8. Replaying a refusal re-evaluates nothing
expected: Re-POSTing the byte-identical body of any refused proposal (from tests 4–7) returns 200 with the same state and reason. No new proposal or proposal_event row is written and no directory lookup happens. A changed body under the same run_id returns 409 idempotency_conflict.
result: pass

### 9. Fail-closed guards and full suite
expected: Setting OLIVE_ENVIRONMENT=production with the fixture directory resolver makes the host refuse to start. OLIVE_DIRECTORY_TIMEOUT_MS=0 or a non-integer refuses start. `dotnet test` (Docker running) passes in full (901), including SubmissionOrderTests (API-08 order), RateLimitTests (12 concurrent at limit 3 → exactly 3 accepted) and the PolicyEndpointTests directory-outage cases (503 directory_unavailable, Retry-After, nothing stored).
result: pass

## Summary

total: 9
passed: 9
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

[none yet]
