# Phase 1: Foundation and Walking Skeleton - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-21
**Phase:** 01-foundation-and-walking-skeleton
**Areas discussed:** Scaffold disposition, Skeleton proposal semantics, Introspection stub and keys, Dev environment and CI

---

## Scaffold disposition

| Question | Options | Selected |
|---|---|---|
| Domain.cs | Delete, re-derive per phase / Move into the new projects / Keep as a frozen reference | Delete, re-derive per phase |
| Old tests | Retire them, keep the naming / Skip with a reason / Port what still applies | Retire them, keep the naming |
| V001 | Rewrite as sql/migrations/0001_init.sql (public schema; proposal + proposal_event) / Rewrite in an `olive` schema / Create all five tables now | Rewrite as 0001_init.sql, public schema |
| Contracts | Leave them for Phase 2 / Move them now / You decide | Leave them for Phase 2 |

## Skeleton proposal semantics

| Question | Options | Selected |
|---|---|---|
| State | `proposed` / `awaiting_approval` | `proposed` |
| Body | Full v1 field set, minimal checks / Raw bytes plus run_id / Fields plus raw body | Full v1 field set, minimal checks |
| Expiry | Key's expires_at in its own column / min(now+8h, key expiry) now | Key's expires_at in its own column |
| Response | 201 + Location, {proposal_id, state} / 200 / You decide | 201 + Location |
| run_id duplicate | Unique constraint now, 409 / No constraint until Phase 2 | Unique constraint now, 409 |

## Introspection stub and keys

| Question | Options | Selected |
|---|---|---|
| Key header | Authorization: Bearer / Configurable header name / Ask nono first | Authorization: Bearer (assumption) |
| Stub keys | Contract fixture files / In-code builder / Both | Contract fixture files |
| 60 s cache | Not yet / Build it now | Not yet |
| Allowlist | Source-controlled config / appsettings / Hard-coded constant | Source-controlled config |

## Dev environment and CI

| Question | Options | Selected |
|---|---|---|
| Local DB | Shared deploy-postgres-1 (D§13) / Olive-owned Compose / Testcontainers only | Shared deploy-postgres-1 |
| PG version | Check it, then pin to match / Pin postgres:17 now | Check it, then pin to match |
| CI | GitHub Actions ubuntu-latest / Local script only / Both | GitHub Actions on ubuntu-latest |
| Run locally | dotnet run plus .env / Containerised / You decide | dotnet run plus .env |

## Claude's Discretion

- How each project is organised internally, and which projects start empty.
- Column names and types in 0001, as long as they meet D§13.
- The shape of problem-details responses, with stable reason codes.
- The format of startup-guard messages; no secrets in them.

## Deferred Ideas

- Introspection cache: deferred to the live popeye transport (v2).
- Confirm the agent-key header name with the nono owner.
- Q14 conflict: `on_behalf_of` is a UPN (NONO_v0.4.3 §5), but the Q14 default says "never UPN". Needs a ruling before Phase 5/7.
- Q4 narrowed: Olive resolves `on_behalf_of` to groups itself (POPEYE §2.5). The mechanism is a Phase 4 decision.
