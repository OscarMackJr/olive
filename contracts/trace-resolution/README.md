# Trace-resolution fixtures

Fixture responses for the hometown trace resolver stub (`FixtureTraceResolver`, non-production only, TRC-09 / FND-05).
`trace-resolution-response.schema.json` is the Olive-proposed contract the fixtures are checked against (seven required
fields, no extras). `index.json` maps a trace ref (`trace://fixture/<case>`) to its response file; only the eight response
files are listed. Each `<case>.proposal.json` is the complete submission body that case binds: its `trace_ref` is the case's
trace ref and its `run_id` is `run-fixture-<case>`. Proposal files are not in the index and are not schema-checked as responses.

## Fixture rules

- `fixture.proposal_hash` equals `Jcs.ProposalHash(proposal)` for every case except `hash-null` (null) and `hash-differs` (wrong).
- Cases: `match`, `run-null-ate03` (ate/0.3, run_id null), `run-differs`, `tenant-differs` (Beta tenant), `agent-null`,
  `agent-differs`, `hash-null`, `hash-differs`. Non-tenant cases use the Alpha tenant and agent `liam`.
- The proposal/fixture pairs are offered to hometown as extra cross-component vectors alongside Q20.

## Tenant scoping (D-17)

The fixture resolver does not tenant-scope lookups: it returns the fixture verbatim. Live hometown returns 404 for
cross-tenant lookups, so verifier check 3 (tenant mismatch) should never fire against a live hometown.

## ITokenIssuer is an ASSUMED shape (D-10)

`ITokenIssuer` is an Olive-proposed [ASSUMED] shape. Open coordination item for the popeye owner, to be confirmed before LIVE-02.
In particular, Olive presents the session key to token issuance (D17.1). POPEYE section 2.2 "no onward use" has not explicitly
confirmed that presenting the session key to issuance is allowed; this is flagged, not assumed settled.

## Known divergence

The non-agent key behaviour (403 from Olive versus POPEYE section 2.1 `active:false` for non-agent keys) is unchanged and goes
to Phase 9 COMPATIBILITY.md.
