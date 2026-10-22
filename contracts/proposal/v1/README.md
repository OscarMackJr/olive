# contracts/proposal/v1

JSON Schema draft 2020-12, format assertion on. Each schema has `<name>.valid.json` and `<name>.invalid.json` beside it.

- `proposal-request.schema.json`: POST /v1/proposals body envelope.
- `proposal.schema.json`: GET /v1/proposals/{id} resource.
- `proposal-submit-response.schema.json`: 201 / 200 reply.

Envelope decision: `run_id`, `action_type`, `party_ref`, `parameters`, `justification`, `trace_ref` are required; `nono_session_id` is optional. The schema is closed (`additionalProperties: false`) and no field accepts `null`.
