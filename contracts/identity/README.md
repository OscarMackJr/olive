# User identity fixtures

Fixture data for `FixtureUserIdentityResolver` (non-production only, FND-05), the stand-in for `IUserIdentityResolver`.
The port is read-only: it takes a tenant id and an opaque `on_behalf_of` and resolves it to exactly one Entra `(oid, tid)`.
It holds no credential and makes no network call.

- `on_behalf_of` stays an opaque UPN-style string on the wire (Q14 ruling, Phase 5 D-06). It is only a lookup key; UPN strings are never compared
  directly. Self-approval is decided by `oid` + `tid` equality (APR-02).
- `user-identity.schema.json` is the Olive-proposed fixture shape, with paired `user-identity.valid.json` and `user-identity.invalid.json`.
- `index.json` lists the embedded fixture files. Lookups are keyed on `(tenant_id, on_behalf_of)`, `on_behalf_of` ordinal and verbatim.
- Ambiguity is represented by two fixture files with the same key (`ambiguous-alpha-1.json`, `ambiguous-alpha-2.json`). The resolver returns Ambiguous and approval refuses fail-closed.
- The live adapter is deferred. Fixtures are barred in production (`INonProductionOnly`).
