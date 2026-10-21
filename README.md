# olive

Status: **Phase 1 (foundation and walking skeleton) is in place: simulation only, no real write path.**
There is no target-system connector and no write-capable credential.

Olive validates and routes externally proposed actions and hosts the shared review queue.
It does not generate proposals, call models, or connect to a target system.  See
[`docs/STATUS.md`](docs/STATUS.md) for entry criteria and open decisions.

The shared vocabulary specification remains a read-only reference at
`~/_agent-inputs/olive/PROGRAM_SHARED_VOCABULARY_v0.4.md`; its machine-readable
counterpart is vendored under `contracts/vendored/`.

## Development

1. Prerequisites: the .NET SDK version pinned in `global.json`, and Docker.
2. Start the shared `deploy-postgres-1` from the Nexus CRM `deploy/` Compose project
   (published on `localhost:5432`; confirmed server version 17.10, image `postgres:17-alpine`).
3. One-time bootstrap as `nexus`:
   - `psql -f sql/bootstrap/001_roles.sql`
   - set the three login-role passwords with psql variables, for example
     `psql -v pw="$OLIVE_APP_PW" -c "ALTER ROLE olive_app PASSWORD :'pw'"`, and the same for
     `olive_migrator` and `olive_reader` (never commit these)
   - `psql -f sql/bootstrap/002_database.sql`
4. Migrate: export `OLIVE_MIGRATOR_URL` in the **shell only**, then
   `dotnet run --project src/Olive.Migrator`. Never put it in `.env`.
5. Run the service: `cp .env.example .env`, edit the placeholder password, then
   `dotnet run --project src/Olive.Host`.
6. Tests: `dotnet test` needs Docker and uses Testcontainers only. Tests never touch the shared
   `deploy-postgres-1` instance.

Notes:

- The agent key is assumed to arrive as `Authorization: Bearer` (D-12); this is isolated in
  `AgentKeyHeader` and awaiting confirmation from the nono owner.
- The service refuses to start if `OLIVE_MIGRATOR_URL` is present, if `OLIVE_ENVIRONMENT` is unset,
  or if a stub is configured in production.
- Migrations are numbered sequentially in order of arrival; the 0002/0003 labels in planning
  documents are nominal.
- CI (`.github/workflows/ci.yml`) restores with `--locked-mode`, builds in Release, runs all tests
  on `ubuntu-latest`, verifies the vendored vocabulary SHA-256 and fails on vulnerable packages.
