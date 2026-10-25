# Service-token fixtures

Fixture responses for the popeye inbound service-token verification stub (`StubServiceTokenVerifier`, non-production only, D-05).
The response shape is unratified (assumption A1); `service-token-response.schema.json` is the contract the fixtures are checked against.
`index.json` maps a visibly fake token (`fixture-service-token-*`) to its fixture file.

Fields are exactly `active`, `principal_type`, `client_id`, `tenant_id`, `expires_at` and an optional `delegated_agent_id`.

## Deliberate divergence

`fixture-key-liam-alpha` (`agent.json`) and `fixture-key-user` (`user.json`) return `active: true` with an `agent` or `user`
principal, so Olive's own principal check (403) runs for them. A real popeye adapter's behavior for non-application principals is
unratified (assumption A1). Inactive, expired and unknown tokens return `{"active": false}` (no probing).
`malformed.json` omits `client_id` on purpose: the stub treats it as an unavailable verifier and fails closed.
