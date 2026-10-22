# Status

**Scaffold only. No real write path exists.** Olive contains a local JSONL simulated
executor and no target-system connector, SDK, HTTP client, or credential.

Outstanding charter entry criteria: approve Olive's existence and boundary; signed
divisional write agreement; selected target system; scoped write credential.

Open charter §6 decisions: codename; language/stack; POC-2 target system; queue
front-end choice; whether Olive's audit also anchors to zt-infra's append-only log.

Catalog decision: catalog is file-only. It is loaded and validated at startup; no
database `action_types` table or runtime add operation exists.

Submission: `POST /v1/proposals` is idempotent on `(agent_id, run_id)` within a tenant.
An identical canonical body (RFC 8785 JCS, SHA-256 `body_sha256`, including
`nono_session_id`) replays 200 with the original `proposal_id` and current state; a
different body gets 409 `idempotency_conflict` and is not stored. The canonical body,
`body_sha256` and `proposal_hash` are stored for audit. `GET /v1/catalog/{version}`
serves the file-backed catalog.
