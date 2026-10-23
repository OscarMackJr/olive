# Directory group fixtures

Fixture data for the directory group stub (`FixtureDirectoryGroupResolver`, non-production only, FND-05).
The stub implements `IDirectoryGroupResolver`, a read-only port: it takes a tenant id and an opaque `on_behalf_of`
and returns the user's directory groups. It holds no credential and makes no network call.

- `directory-groups.schema.json` is the Olive-proposed fixture shape (`tenant_id`, `on_behalf_of`, `groups`).
- `index.json` lists the fixture files that are embedded and loaded. Lookups are keyed on `(tenant_id, on_behalf_of)`,
  `on_behalf_of` compared ordinal and verbatim (D-06).
- `analyst-alpha.json` and `analyst-beta.json` match the `on_behalf_of` and tenant of the introspection fixtures
  and grant `ent:crm.case_note.write`.
- Q14 (the real directory source) is open. Fixtures are barred in production (`INonProductionOnly`).
