# Phase 6: Simulated Execution and Audit Chain - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-23
**Phase:** 06-simulated-execution-and-audit-chain
**Areas discussed:** Execution trigger, Failure & refusal states, JSONL target output, Chain view & beat 2 demo

---

## Execution trigger

| Option | Description | Selected |
|--------|-------------|----------|
| In-request, after commit | Approval commits, then the endpoint calls the executor in the same request | ✓ |
| Background channel worker | Approval enqueues the proposal on a Channel and a hosted service drains it | |
| You decide | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Advisory lock + unique ledger | `pg_advisory_xact_lock` per (tenant, proposal) plus unique ledger key | ✓ |
| Row lock on proposal | `SELECT … FOR UPDATE` held across the attempt | |
| You decide | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Startup only | Per Q5 | ✓ |
| Startup + periodic timer | Re-scan every N minutes | |

| Option | Description | Selected |
|--------|-------------|----------|
| State only | Proposal GET shows `executed` or `execution_failed`; no shape change | ✓ |
| Add optional execution fields | Additive `execution` object on the proposal response | |

## Failure & refusal states

| Option | Description | Selected |
|--------|-------------|----------|
| Yes, with outcome | Every terminal attempt writes a ledger record with an outcome and a static reason | ✓ |
| Success only | The ledger records target writes only | |

| Option | Description | Selected |
|--------|-------------|----------|
| execution_failed + reason | Terminal, `catalog_invalid`, no target write | ✓ |
| Leave approved | Skip without changing state | |

| Option | Description | Selected |
|--------|-------------|----------|
| expired | Existing Approved→Expired edge, reason `proposal_expired` | ✓ |
| execution_failed | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Fail closed, never re-write | A started row with no finish becomes `execution_failed` / `execution_in_doubt` | ✓ |
| Reconcile from the JSONL | Read the target back to decide | |

**Notes:** Claude surfaced a conflict. An INSERT-only, one-row-per-proposal ledger can't record both an "attempt started" row before the target write and an outcome afterwards. Follow-up:

| Option | Description | Selected |
|--------|-------------|----------|
| Start + finish rows | Up to two INSERT-only rows, unique on (tenant, proposal, phase) | ✓ |
| One row; outcome on proposal_event | A single pre-write row, with the outcome kept in state and event | |
| One row after the target write | Can't detect in-doubt attempts without the JSONL | |

## JSONL target output

| Option | Description | Selected |
|--------|-------------|----------|
| The would-be write | Ids, target, mode, time, and `would_send` = parameters | ✓ |
| References only | No parameters | |

| Option | Description | Selected |
|--------|-------------|----------|
| One file, configured path | `tenant_id` on every line; startup fails without the config | ✓ |
| One file per tenant | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Flush + fsync first | Before the finished row commits | ✓ |
| Flush only | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Allowed, always labelled | Phase 1 is simulation-only by Decision | ✓ |
| Barred like fixtures | | |

## Chain view & beat 2 demo

| Option | Description | Selected |
|--------|-------------|----------|
| Rationale yes, parameters no | Meets EXE-06 without exposing content | ✓ |
| Everything | | |
| References only | Does not meet EXE-06 literally | |

| Option | Description | Selected |
|--------|-------------|----------|
| olive_reader + olive_app | `security_invoker` view, so RLS binds both | ✓ |
| olive_reader only | | |

| Option | Description | Selected |
|--------|-------------|----------|
| xUnit + transcript, like Beat 4 | `Demo=beat2`, `build/demo-beat2.sh` | ✓ |
| Against a running host | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Yes, one view query | The exact SELECT, run as `olive_reader`, and its row | ✓ |
| Assembled from several reads | | |

## Claude's Discretion

- How the sweep enumerates tenants under forced RLS, the ledger and view names, the reason-code constants, replacing execution-record v0 with v1, the fault-injection seam, project placement, and where the executor is called after commit.

## Deferred Ideas

- Periodic sweep timer; optional execution fields on the proposal response; reconciling from the JSONL; the UI audit page (Phase 7).
