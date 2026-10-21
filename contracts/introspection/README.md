# Introspection fixtures

Fixture responses for the popeye key-introspection stub (`StubKeyIntrospector`, non-production only, D-13).
Shape follows POPEYE_v0.4.2 section 2.1; `introspection-response.schema.json` is the contract the fixtures are checked against.
`index.json` maps a visibly fake key (`fixture-key-*`) to its fixture file.

## Deliberate divergence

POPEYE_v0.4.2 section 2.1 and D-13 say a non-agent key returns only `active: false`. `user.json` and `application.json`
instead return `active: true` with their `principal_type`, so Olive's own principal check
(403 `principal_not_permitted`, D section 7 / Phase 1 success criterion 1) is exercised.
Flagged for the Phase 3 popeye research.

Inactive, expired and unknown keys all return exactly `{"active": false}` (no key-existence probing).
