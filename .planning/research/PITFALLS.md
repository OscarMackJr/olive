# Pitfalls Research

**Domain:** Governed action broker and review queue (.NET 10 / ASP.NET Core / Razor Pages / PostgreSQL + Npgsql / xUnit v3 + Testcontainers), simulation-only Phase 1
**Researched:** 2026-10-21
**Confidence:** MEDIUM-HIGH overall. PostgreSQL RLS, privilege and GUC behaviour, and the .NET JSON and logging defaults were checked against official docs or the PostgreSQL mailing lists. Items marked *(LOW)* rely on training data only and should be verified by a spike.

Phase names below refer to the following suggested roadmap phases. The roadmapper can renumber them.

| Tag | Suggested phase |
|-----|-----------------|
| **P-FND** | Foundation: project split, CI, Testcontainers harness, `.gitattributes`, startup guards skeleton |
| **P-DB** | Persistence: roles, migration runner, forced RLS, repositories, layer-removal tests |
| **P-CON** | Contract: v1 schemas, OpenAPI, raw-body pipeline, JCS canonicalizer, non-mutating validation |
| **P-SUB** | Submission path: introspection stub, principal checks, idempotency, trace resolver and token client, policy |
| **P-LIFE** | Lifecycle: IR-1 queue, state machine, expiry, simulated executor, ledger |
| **P-UI** | Reviewer UI: Razor Pages, OIDC and stub IdP, decide flow |
| **P-EVD** | Evidence: §16.8 / §17.4 / TC suites, credential audit, session-key capture, demo scripts |

---

## Critical Pitfalls

### Pitfall 1: RLS tests that pass because the test connects as a superuser or the owner

**What goes wrong:**
The "remove one layer" test (REQ-QUE-02) goes green, but it never exercised RLS. `FORCE ROW LEVEL SECURITY` binds the table owner. It does **not** bind superusers or roles with `BYPASSRLS`. The Testcontainers Postgres module's default user (`postgres`) is a superuser. So is `nexus` on `deploy-postgres-1`. Any test or service that connects with the container's default connection string bypasses every policy.

**Why it happens:**
`PostgreSqlContainer.GetConnectionString()` is the easiest string to reach for. The migration then "works", and the queries return the right tenant's rows because the application predicate is still present.

**How to avoid:**
- The harness bootstraps once as the superuser: it creates `olive_owner` (NOLOGIN), `olive_migrator`, `olive_app` (`NOSUPERUSER NOBYPASSRLS`) and `olive_reader`. Migrations then run as `olive_migrator` and every test uses `olive_app` or `olive_reader`. Expose only those connection strings from the fixture.
- Add a guard test: as `olive_app`, `SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user` must return `false, false`.
- Add a service startup check that refuses to run if the connected role is a superuser, has `BYPASSRLS`, or owns the tables (`pg_tables.tableowner = current_user`).
- Seed **two** tenants in every isolation test. Also assert that the caller's own tenant returns more than zero rows. Otherwise an empty result passes vacuously, for example when the GUC fails and nothing is returned.

**Warning signs:**
Tests use `container.GetConnectionString()` directly. No test asserts `current_user`. Isolation tests seed only one tenant. The isolation test still passes when you comment out the policy.

**Phase to address:** P-FND (harness exposes role-specific strings) and P-DB (guard tests and startup check).

---

### Pitfall 2: Objects created by `olive_migrator` are owned by `olive_migrator`, not `olive_owner`

**What goes wrong:**
Decision §13 says `olive_owner` owns everything and `olive_migrator` is a *member* of it. Membership does not change who owns new objects. Tables created by the migrator are owned by `olive_migrator`. Then `FORCE RLS` binds the wrong role, later `ALTER DEFAULT PRIVILEGES` statements don't match, and handing off to a dedicated server leaves objects owned by a login role.

**How to avoid:**
- The runner executes `SET ROLE olive_owner;` at the start of each migration transaction.
- Use `ALTER DEFAULT PRIVILEGES FOR ROLE olive_owner ...` to grant on future objects.
- Add a test that asserts every table, sequence and function in the `olive` schema has `pg_class.relowner = 'olive_owner'::regrole`.
- Keep role creation and passwords **out** of the forward-only migrations. Roles are cluster-wide, and `deploy-postgres-1` is shared with Nexus, loan_engine, sit-cip and hometown. Put role creation in a separate idempotent bootstrap script (`DO $$ ... IF NOT EXISTS (SELECT FROM pg_roles ...)`) run by `nexus`. `CREATE ROLE IF NOT EXISTS` does not exist.

**Warning signs:** `\dt` shows `olive_migrator` as owner. A migration contains `CREATE ROLE` or `PASSWORD`.

**Phase to address:** P-DB.

---

### Pitfall 3: `SET LOCAL` / `current_setting` behave differently on pooled connections

**What goes wrong (several related traps):**
1. **`SET LOCAL` cannot take a bind parameter.** Developers build `$"SET LOCAL olive.tenant_id = '{tenantId}'"`, which is an injection point. Use `SELECT set_config('olive.tenant_id', @tenant, true)` instead. The third argument `true` is transaction-local.
2. **`SET LOCAL` outside an explicit transaction is a no-op with a WARNING.** Npgsql autocommits each command. The "fix" someone then reaches for is plain `SET`, which is session-scoped. A session-scoped tenant survives on that physical connection until Npgsql resets it. Npgsql normally resets with `DISCARD ALL`, or the equivalent minus `DEALLOCATE` when prepared statements exist. That reset is skipped if `No Reset On Close=true`, and there are known edge cases where a connection closes inside an uncommitted ambient `TransactionScope`. The result is cross-tenant reads.
3. **"Without `missing_ok`, an unset tenant raises an error" is only true on a fresh connection.** Once a custom GUC has been set on a session, even with `SET LOCAL` in a transaction that rolled back, it stays defined and reads back as `''` for the rest of that connection. So on a reused pooled connection, `current_setting('olive.tenant_id')` returns `''`, and the `::uuid` cast raises `invalid input syntax for type uuid`. That still fails closed, but with a different error. Never "fix" it with `NULLIF(current_setting(..., true), '')::uuid` plus a `COALESCE`, which turns fail-closed into silently empty or, worse, into a default.
4. Read-only SELECTs need the tenant too, including the reviewer UI list queries and the `olive_reader` audit queries.

**How to avoid:**
- Route every DB call through a single `ITenantSession` / unit-of-work. It opens a connection from one `NpgsqlDataSource`, begins a transaction, runs `set_config(..., true)`, and only then hands the transaction to repositories. Repositories take an `NpgsqlTransaction` (or a wrapper), never a raw `NpgsqlConnection`, so there is no API path that skips the tenant.
- Never use `TransactionScope` with Npgsql here.
- Never set `No Reset On Close`.
- Test the unset-tenant error on a **fresh** connection and on a **reused pooled** connection (set the tenant in transaction A, then query without it in transaction B on the same connection with `Max Pool Size=1`). Assert an exception in both cases, not an empty result.
- Add a regression test with `Max Pool Size=1`: tenant A transaction, then tenant B transaction, and assert B sees none of A's rows.

**Warning signs:** String interpolation near `SET`. Repository methods that accept `NpgsqlConnection`. Any `current_setting(..., true)` in a policy. Isolation tests that only ever use fresh connections.

**Phase to address:** P-DB.

---

### Pitfall 4: A single `FOR ALL` policy plus loose grants makes the "append-only" ledger mutable, and RETURNING or execution-replay breaks on INSERT-only grants

**What goes wrong:**
- The scaffold's `FOR ALL` policies allow UPDATE and DELETE wherever the grant does. The real append-only control is the **grant**. Policies only filter rows. If `olive_app` inherits `UPDATE` through a broad `GRANT ALL` or default privileges, the ledger is mutable.
- `INSERT ... RETURNING` needs `SELECT` privilege on the returned columns, and with RLS a `SELECT` policy must pass for the new row. An INSERT-only `execution_ledger` therefore fails on `RETURNING`.
- REQ-ACT-03 requires "a retried execution returns the first result", which means `olive_app` must read the ledger. Decision §13 allows "SELECT where the service must read its own history". Forget that grant and execution idempotency can't be implemented. Grant too much and append-only is lost.
- `INSERT ... ON CONFLICT DO UPDATE` needs `UPDATE` privilege and checks UPDATE policies. Don't use it on INSERT-only tables. *(LOW)*: whether `ON CONFLICT (cols) DO NOTHING` needs `SELECT` on the conflict-target columns has not been verified. Spike it.
- `TRUNCATE` is not subject to RLS. Only the grant stops it.
- Foreign-key checks run without RLS. An FK on `proposal_id` alone lets a tenant-A row reference a tenant-B proposal. Use composite keys `(tenant_id, proposal_id)` and composite FKs.
- Views run with the view owner's rights unless `security_invoker = true` (PG15+). An `olive_reader` audit view owned by `olive_owner` checks policies as the owner; whether FORCE then filters correctly by the reader's tenant setting is subtle, so do not rely on it: use `security_invoker = true` and test as `olive_reader`. `SECURITY DEFINER` functions have the same problem.

**How to avoid:**
- Write separate policies per command: `FOR SELECT USING (...)` and `FOR INSERT WITH CHECK (...)` on `execution_ledger` and `proposal_event`. Use `FOR SELECT/INSERT/UPDATE` on `proposal` and `queue_item`. Add **no** UPDATE or DELETE policy on the append-only tables, so if an UPDATE grant ever leaks, RLS still matches zero rows.
- Use explicit per-table `GRANT`s with no `GRANT ALL`, and `REVOKE ALL ... FROM PUBLIC`.
- Add a privilege test that queries `information_schema.role_table_grants` / `has_table_privilege` and asserts the exact matrix from Decision §13 for all four roles. Then attempt `UPDATE`, `DELETE` and `TRUNCATE` on the ledger as `olive_app` and assert `42501 insufficient_privilege`.
- Use `security_invoker` views only, or no views. Use no `SECURITY DEFINER` functions.
- Pin the Testcontainers image to the same major version as `deploy-postgres-1` (check with `SELECT version()`), because `security_invoker` and `NULLS NOT DISTINCT` need PG15+.

**Warning signs:** `FOR ALL` in a migration. `GRANT ALL`. Single-column FKs. Execution-replay code that "works" only in unit tests with in-memory stores.

**Phase to address:** P-DB (grants and policies) and P-LIFE (ledger read path for execution replay).

---

### Pitfall 5: The bytes that are hashed are not the bytes that were received (ASP.NET Core model binding mutates)

**What goes wrong:**
Decision §17.2 item 4 forbids coercion, defaults, key stripping, trimming and normalisation before hashing. ASP.NET Core's default JSON input uses `JsonSerializerDefaults.Web`, which means `PropertyNameCaseInsensitive = true` and `NumberHandling = AllowReadingFromString`. Binding the request to a C# record will therefore:
- accept `"5"` where an int is expected. §17.4 requires a `400`;
- accept `RUN_ID` or `Run_Id` as `run_id`;
- silently drop unknown keys, which §17.4 requires to be hashed, never stripped;
- apply C# property initialisers as defaults;
- take the last value of a duplicated key, because duplicates are allowed by default.

Re-serialising that DTO and hashing it produces a hash that differs from hometown's for honest proposals, or matches for altered ones.

**How to avoid:**
- `POST /v1/proposals` reads `Request.Body` as raw bytes, with a size cap and `Content-Type: application/json` required. It parses once into a `JsonDocument` with strict options: comments disallowed, trailing commas disallowed (both are the default, so never turn them on) and duplicate properties **rejected**. .NET 10 adds `AllowDuplicateProperties` (default `true`) to `JsonSerializerOptions` and, per the API catalog, to `JsonDocumentOptions`. If it isn't available on the `JsonDocumentOptions` type in the shipped SDK, detect duplicates in a `Utf8JsonReader` pre-pass.
- The JSON Schema validator, the JCS canonicalizer and the four-key proposal-hash extraction all read from **that same `JsonElement`**. Only after all three pass do you map to domain types.
- Never use `[FromBody] ProposalDto` on this endpoint. Don't change `ConfigureHttpJsonOptions` globally either, because the UI and the GET endpoints can keep the defaults.
- Note the API inconsistency: `JsonDocument` keeps both duplicates, `JsonSerializer` is last-wins, and `JsonNode`/`JsonObject` throws. A validator built on `JsonNode` can therefore disagree with a hasher built on `JsonElement`. One parse, one representation.

**Warning signs:** A proposal DTO with `[FromBody]`. Any `JsonSerializer.Serialize(dto)` feeding a hash. The `"5"` test passing with a `200`.

**Phase to address:** P-CON.

---

### Pitfall 6: A homemade "JCS" that is not RFC 8785, so the hash never matches hometown (TypeScript)

**What goes wrong:**
The olive and hometown hashes disagree for some inputs. Every one of those becomes `trace_proposal_mismatch`, a **stored, permanent** refusal for that run. Typical deviations:
- **Numbers.** RFC 8785 requires the ECMAScript `Number.prototype.toString` form of the IEEE-754 double: `1.0` → `1`, `1e21` → `1e+21`, `0.0000001` → `1e-7`, `-0` → `0`. Copying `JsonElement.GetRawText()` keeps `1.0` and `1E2` as written. .NET `double.ToString("R")` gives `1E+21`. Integers above 2^53 lose precision in JS, so hometown hashes the rounded value.
- **Strings.** Only `"`, `\` and controls below U+0020 are escaped: `\b \t \n \f \r` with short forms, and others as lowercase `\u00xx`. Everything else, including `/`, `<`, `>`, `&`, `'`, `+` and all non-ASCII, is emitted raw as UTF-8. `Utf8JsonWriter` with the default `JavaScriptEncoder` escapes HTML-sensitive characters and non-ASCII. `UnsafeRelaxedJsonEscaping` still escapes some characters. Don't use `Utf8JsonWriter` to produce JCS output.
- **Key order.** Sort by UTF-16 code units. `string.CompareOrdinal` on .NET strings is correct. Sorting UTF-8 bytes gives a different order for supplementary-plane against U+E000–U+FFFF keys. `StringComparer.InvariantCulture` or `OrderBy(k => k)` with the default comparer is wrong.
- **No Unicode normalisation.** NFC and NFD forms hash differently by design.
- **Lone surrogates** (`"\uD800"`) must be rejected. Check what `JsonElement.GetString()` does with them and turn it into a `400` *(LOW: behaviour not verified)*.
- **Two different hashes are conflated.** `body_sha256` (§15.3: the whole v1 body, for idempotency) and `proposal_hash` (§17.2: exactly four keys `action_type`, `party_ref`, `parameters`, `justification`, for trace binding) are different values. Mixing them up breaks either idempotency or binding.

**How to avoid:**
- Write a small, isolated `Jcs` class with its own tests, or adopt the cyberphone reference implementation (`cyberphone/json-canonicalization`, which has a C# version) *(MEDIUM: confirm the package and licence)*.
- Port the RFC 8785 / cyberphone test vectors, including the number-formatting vectors.
- **Create a cross-language golden fixture set with hometown**: inputs plus expected hex hashes produced by hometown's TS canonicalizer, committed under `contracts/trace-resolution/`, with both sides' CI asserting them. Include `1.0`, `1e21`, `"<&>"`, emoji keys, a key in U+E000, a string containing U+2028 LINE SEPARATOR, nested empty objects and arrays, and a large integer.
- Constrain numbers in the v1 schema and catalog parameter schemas where possible: integers with `maximum` ≤ 2^53−1, and prefer strings for identifiers. That removes most of the number risk.

**Warning signs:** The canonicalizer uses `GetRawText()` or `Utf8JsonWriter`. There is no shared fixture with hometown. The tests only check key reordering and whitespace, which is the easy §17.4 case.

**Phase to address:** P-CON, with the hometown golden fixture as an explicit deliverable before P-SUB.

---

### Pitfall 7: The JSON Schema validator silently does less (or more) than you think

**What goes wrong:**
- In draft 2020-12, `format` (for example `uuid`, `date-time` or `uri`) is an **annotation by default**. JsonSchema.Net's `EvaluationOptions.RequireFormatValidation` defaults to `false`, so `"tenant_id": "not-a-uuid"` passes.
- Validators don't apply `default`, but code generated from schemas (NJsonSchema / NSwag DTOs) and DTO initialisers do, which reintroduces Pitfall 5.
- NJsonSchema targets older drafts and is incomplete for 2020-12. Newtonsoft.Json.Schema is commercially licensed beyond a quota.
- `$ref` across files (OpenAPI → `contracts/proposal/v1/*.json`, catalog parameter schemas) fails or fetches over the network unless every schema is pre-registered by `$id`. An unresolved `$ref` can error at runtime instead of at startup.
- The JSON-Schema `integer` type accepts `1.0`. That is fine as long as JCS (Pitfall 6) normalises it identically on both sides.
- `additionalProperties: false` at the top level, combined with an *open* `parameters` object, is exactly what §17.4 relies on: unknown keys inside `parameters` are allowed and hashed. Don't "tighten" the parameters schema without agreeing it with hometown and Liam.

**How to avoid:**
- Use JsonSchema.Net (json-everything) with `RequireFormatValidation = true` and `OutputFormat = List` so error paths are useful. Pre-register every schema by `$id` at startup and fail startup on any unresolved `$ref`.
- Validate catalog files against `action-type.schema.json` at startup (CONCERNS: currently never applied).
- Add a contract test that every `contracts/examples/*.valid.json` passes and every `*.invalid.json` fails, including a bad-`uuid` case that only fails when format validation is on.
- Keep `openapi.yaml` hand-written with `$ref`s to the schema files. Don't let `Microsoft.AspNetCore.OpenApi` generate it from C# types, because that is a second source of truth that drifts.

**Warning signs:** No test with an invalid `format` value. Schemas loaded lazily on first request.

**Phase to address:** P-CON.

---

### Pitfall 8: The idempotency race is handled with "catch unique violation, then SELECT" inside the same transaction

**What goes wrong:**
- In PostgreSQL, a `23505 unique_violation` aborts the transaction. The follow-up SELECT fails with `25P02 in_failed_sql_transaction`, and the loser of a concurrent first submission returns a 500 instead of re-entering step 3 (§15.2).
- Under `REPEATABLE READ` / `SERIALIZABLE`, which the scaffold migration footer suggests, the loser's snapshot may not see the winner's row even after the conflict. You get `40001` serialization failures that need a retry loop everywhere.
- Holding a DB transaction open across the trace-resolver and popeye calls (seconds, possibly a `503`) pins pooled connections and leaves `idle in transaction` sessions. Under load that exhausts the pool, which fails *closed* but looks like an outage.

**How to avoid:**
- Treat steps 1–6 as **no DB transaction**, apart from the step-3 lookup, which is a short read-committed read. Step 7 is one short transaction: `INSERT ... ON CONFLICT (tenant_id, agent_id, run_id) DO NOTHING RETURNING proposal_id`. If no row comes back, **commit or roll back, then** re-run the step-3 lookup in a new statement and return `200` if the hashes match or `409` if they differ. Under READ COMMITTED, `ON CONFLICT` waits for the in-flight winner, so the follow-up read sees the committed row.
- If any transaction runs as SERIALIZABLE, wrap it in a bounded retry on `40001` / `40P01` (`PostgresException.IsTransient` or check `SqlState`). Prefer READ COMMITTED plus conditional updates (Pitfall 10).
- Test with N parallel identical first submissions: exactly one proposal row, one queue item and one `proposal_event`, and every caller gets `200` with the same `proposal_id`. Run the same test with differing bodies: one wins and the rest get `409`.

**Warning signs:** `catch (PostgresException e) when (e.SqlState == "23505")` followed by a query on the same transaction. `HttpClient` calls inside a `using var tx`.

**Phase to address:** P-SUB.

---

### Pitfall 9: The stored-vs-not-stored outcomes get mixed up, so retries return the wrong thing

**What goes wrong:**
The ratified matrix is subtle. Get it wrong and a transient failure becomes permanent, or a permanent refusal becomes re-evaluable.

| Outcome | Stored under `(agent_id, run_id)`? | Retry with same run_id |
|---|---|---|
| `401` / `403 principal_not_permitted` | No | Re-evaluated (auth always runs first) |
| `409 idempotency_conflict` | No (logged, content-free) | `409` again |
| `400` schema failure | No | Re-evaluated |
| `422 trace_not_found` | No | Re-evaluated; **never negatively cached** |
| `503` (resolver unavailable, hometown or popeye 401/403, token mismatch) | No | Re-evaluated |
| `refused_policy` (`not_in_catalog`, `trace_unresolved`, `trace_tenant_mismatch`, `trace_agent_mismatch`, `trace_proposal_mismatch`, purpose, entitlement, **rate**) | **Yes** | `200` with the stored refusal |
| Accepted | Yes, plus queue item | `200` with **current** state |

Specific traps:
- **Ordering ambiguity in the ratified text.** The idempotency lookup (step 3) runs *before* schema validation (step 4), but `body_sha256` is defined over the body "after schema validation". What does a *malformed* body under an existing `run_id` return: `409` or `400`? Pin this down with the ratifier. A suggested reading: unparseable JSON or a missing or non-string `run_id` is `400` before step 3, and otherwise step 3 hashes the parsed body regardless of schema validity. Record the answer in COMPATIBILITY.md.
- **Rate limiting.** CONCERNS records that refusals and retries consume budget, and that the limiter key lacks the tenant. A `200` replay must **not** count. A rate refusal is stored, so that `run_id` is refused permanently even after the window passes. That follows from the spec, so document it for Liam. The count must come from the DB (`proposal` rows per `(tenant_id, agent_id, action_type)` in the window), never from in-process `System.Threading.RateLimiting`, which is per-instance, isn't persisted, and doesn't follow `FakeTimeProvider`. Two concurrent submissions can both pass the count check, so serialise with `pg_advisory_xact_lock(hash(tenant, agent))` inside the step-7 transaction and re-check the count there.
- **Unknown catalog action.** This must be stored as `refused_policy/not_in_catalog`, not thrown (CONCERNS). Parameter-schema validation needs the catalog entry, so order the work as: v1 envelope schema, then catalog lookup (unknown → stored refusal), then parameter schema (fail → `400`). Confirm that order against §15.2.
- **Trace cache.** Cache only `200` resolutions, keyed by `(tenant_id, trace_id)`, never `404`/`503`/`401`/`403`. A cache keyed only on `trace_id` crosses tenants.

**How to avoid:** Encode the table above as a single `SubmissionOutcome` type with an explicit `IsStored` property, and write one parameterised test per row that calls twice with the same `run_id`.

**Warning signs:** Refusal storage decided ad hoc in several `if` branches. The rate counter incremented before the idempotency lookup.

**Phase to address:** P-SUB.

---

### Pitfall 10: State-machine races on claim, decide, approve and execute (two state machines, one truth)

**What goes wrong:**
- Read-check-write (`SELECT state` → `if awaiting_approval` → `UPDATE`) lets two reviewers both approve, or approve and reject concurrently. The scaffold already allows re-approval of executed proposals.
- The IR-1 queue (`open → claimed → decided | withdrawn`; `accept | reject | defer`) and the proposal lifecycle (`awaiting_approval → approved | rejected → executed | execution_failed | expired`) are **separate** state machines. Updating them in separate transactions leaves a queue item `decided/accept` with a proposal still `awaiting_approval`, or a proposal `expired` with a live `open` item. The scaffold already does the second.
- `defer` returns to `open`. A naive "decided items are immutable" rule breaks defer, and a naive overwrite loses the decision history that IR-1 requires.
- Expiry between approve and execute is easy to forget. Execute must re-check expiry.
- Abandoned claims leave items stuck `claimed` forever, and nothing in Phase 1 releases them.

**How to avoid:**
- Make every transition a conditional update with a row-count check, in one transaction that also inserts `proposal_event` (and the queue history row):
  `UPDATE proposal SET state='approved', approved_by=@r, version=version+1 WHERE tenant_id=@t AND proposal_id=@p AND state='awaiting_approval' AND expires_at > @now` → 0 rows means re-read and return a typed `409 invalid_state` or `410/422 proposal_expired` (a distinct error, REQ-ACT-05).
- The queue item and proposal transition in the **same** transaction. Write a test asserting the mapping invariant across all transitions: for kind `action_approval`, `decided/accept` ⇔ `approved|executed|execution_failed`.
- Decide whether `decide` requires the decider to be the claimant. IR-1 needs reading on this. Decide it, test it, and record it.
- Use a `version` integer column rather than `xmin` (simpler with Dapper or raw Npgsql, and survives `VACUUM FULL`).
- Avoid SERIALIZABLE unless you also build retries (Pitfall 8).
- Add concurrency tests: two parallel approvals → one success and one `409`; approve ∥ reject → one wins; approve with the clock past expiry → expired error and no event row claiming approval.

**Warning signs:** Any transition method that doesn't check rows-affected. Queue and proposal repositories each opening their own transaction.

**Phase to address:** P-LIFE.

---

### Pitfall 11: Execution "exactly once" breaks on the INSERT-only ledger and the JSONL side effect

**What goes wrong:**
- The scaffold writes JSONL, then records. A crash in between, or a retry, writes a duplicate. That is the failure Phase 2 must not inherit when JSONL becomes a CRM.
- With an INSERT-only ledger you can't write a `pending` row and update it to `executed`. Teams then either grant `UPDATE` (breaking append-only) or skip the intent record (losing crash safety).
- `execution_failed` is never produced, so the failure path is untested.

**How to avoid:**
- Serialise execution per proposal: `SELECT ... FROM proposal WHERE ... AND state='approved' FOR UPDATE` (or `pg_advisory_xact_lock`), with the ledger unique on `(tenant_id, proposal_id)`. A retry reads and returns the existing ledger row, which needs the ledger SELECT grant (Pitfall 4).
- Pass `proposal_id` as an **idempotency key to the executor interface** and make the simulated target honour it (skip if already written). That is the contract the real Phase 2 executor must also satisfy, so design it now.
- If you need an intent record, use two INSERT-only rows (`attempt`, `result`) or `proposal_event` rows. Never an UPDATE.
- Add a fault-injection test: the executor succeeds, then the commit fails, then a retry → the target has exactly one write and the ledger has one result.
- Add a test for the failure path: executor throws → `execution_failed` stored, and a retry returns the first result (per REQ-ACT-03), not a re-run. Confirm with the ratifier whether `execution_failed` is terminal.

**Phase to address:** P-LIFE.

---

### Pitfall 12: Session key or service token leaks through logs, records or exceptions

**What goes wrong:** §17.4 requires "token absent from every log line and stored row" and "session key appears only on calls to popeye". These are common leak paths in ASP.NET Core:
- **C# `record` `ToString()` prints every property.** `logger.LogInformation("Introspecting {Req}", req)`, where `req` is `record IntrospectionRequest(string SessionKey, ...)`, leaks the key. Exception messages built with `$"{request}"` do the same.
- **Request logging.** `AddHttpLogging` redacts headers outside its allowlist. Someone adding `Authorization` to `RequestHeaders` while debugging leaks the key. `UseConnectionLogging` (Kestrel) at Trace dumps raw bytes. `UseDeveloperExceptionPage` and ProblemDetails with exception detail echo headers and messages.
- **HttpClient logging.** Since .NET 9, `IHttpClientFactory` logging redacts **all** header values by default, but it logs the **request URI** at Information. Never put a token or key in a query string. `AddExtendedHttpClientLogging` (Microsoft.Extensions.Http.Diagnostics) can log bodies, and RFC 7662-style introspection sends the key in the **body**. OpenTelemetry HTTP instrumentation records `url.full`.
- **`new HttpClient()` outside the factory** bypasses the capture handler, so the §17.4 "capture every outbound call" test passes vacuously.
- **Exception text from Npgsql** includes key values on constraint violations (`Key (tenant_id, agent_id, run_id)=(...)`). That isn't a secret, but don't return it to clients. Keep Npgsql parameter logging (`EnableParameterLogging`) off.
- **Persistence.** It's tempting to store the token, or a "token hash for debugging", on the proposal row. Don't. Store `key_id` only.

**How to avoid:**
- Wrap secrets in `sealed class SessionKey` / `ServiceToken` types. `ToString()` returns `"[redacted]"`, there is no implicit string conversion, a `JsonConverter` throws on serialise, and the value is reachable only through an explicit `Reveal()` used in exactly two places (the popeye client and the hometown client's `Authorization` header).
- Register every outbound client through `IHttpClientFactory` with a shared `CapturingHandler` in tests. Add an architecture test (reflection or Roslyn) that no `src/` code calls `new HttpClient(` and that the API project has no HTTP or SDK `PackageReference` beyond the allowlist. This also replaces the weak `REQ_ACT_04` substring test.
- Make the leak test meaningful: use a high-entropy sentinel key (for example `sk_TEST_7f3c...`), set **all** log levels to Trace in an in-memory `ILoggerProvider`, run the full §16.8 / §17.4 suite, then grep logs, the JSONL, and **every column of every table** (`SELECT row_to_json(t)::text`) for the sentinel. At Information level the test passes vacuously.
- Don't cache introspection results. §15.2 says authentication always runs first, and a revoked key must stop working immediately.

**Warning signs:** `string sessionKey` parameters threaded through many layers. Log statements with `{@Request}` destructuring. The leak test runs at default log level.

**Phase to address:** P-FND (secret types, analyser and arch tests), P-SUB (clients), P-EVD (capture and grep evidence).

---

### Pitfall 13: Service-token cache keyed or expired wrongly

**What goes wrong:**
- Keying the cache by `(tenant_id, agent_id)` only lets a token outlive the session key that justified it. §17.1 explicitly forbids this: the key must include `key_id`.
- Cache lifetime is computed from the token's `exp` only, not `min(token.exp, key.expires_at) − skew`.
- A token is cached **before** the claims check (`delegated_agent_id` and `tenant_id` must equal olive's introspection). It must be discarded with a `503` and an alarm, never cached.
- `IMemoryCache` eviction is relied on for correctness. Eviction is lazy and uses its own clock, so tests with `FakeTimeProvider` don't move it.
- Popeye failures are cached, or a token the stub returned with expiry later than the key is accepted (§17.4: reject, never cache).

**How to avoid:** Write a tiny purpose-built cache. Use a `ConcurrentDictionary<(Guid tenant, string agent, string keyId), Entry>` and check `entry.UsableUntil > timeProvider.GetUtcNow()` **on every read**, with `UsableUntil = min(token.exp, key.expires_at) − margin`. Run the claims check before insert. Write one test per §17.4 token row.

**Phase to address:** P-SUB.

---

### Pitfall 14: Fixture resolver, popeye stub or stub IdP reaching a real environment

**What goes wrong:**
- `IHostEnvironment` defaults to **Production** when `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` is unset, while `OLIVE_ENVIRONMENT` must have **no default** (§16.5). A guard written against `IsProduction()`, or one that defaults `OLIVE_ENVIRONMENT` to `local`, violates the spec.
- The guard checks a config flag (`TraceResolver:Mode=fixture`) instead of the **actual registered implementation**. A later DI change registers the fixture resolver unconditionally and the guard still passes.
- Only the trace fixture is guarded. The **stub IdP** in production means anyone can approve. The **popeye introspection stub** means anyone is Liam. Both are at least as dangerous as fixtures.
- The guard runs in a hosted service whose relative start order with Kestrel isn't guaranteed to block traffic.
- `WebApplicationFactory` defaults to the `Development` environment, and tests swap in stubs via `ConfigureTestServices`, so the "refuses to start" test can be bypassed by the test harness itself.
- `trace_verification` is set from configuration instead of from the resolver that actually answered.

**How to avoid:**
- Every stub implements a marker interface `INonProductionComponent`. In `Program.cs`, after `builder.Build()` and **before** `app.Run()`, resolve `OLIVE_ENVIRONMENT`. If it is missing or invalid, exit non-zero. If it is `production`, enumerate the registered implementations of `ITraceResolver`, `IPopeyeIntrospection`, `IServiceTokenClient`, the auth schemes and the store interfaces, and exit if any is an `INonProductionComponent`.
- `trace_verification` comes from a property on the resolver instance that answered (`resolver.VerificationSource`).
- Test the guard by launching the built host with `OLIVE_ENVIRONMENT=production` and the default stub wiring (via `WebApplicationFactory` with **no** service overrides, or a `dotnet <dll>` process test), and assert a non-zero exit. Do the same with the variable unset.
- Record the reviewer auth source (`entra | stub`, from the `iss` claim) on every decision, in the same way as `trace_verification`.

**Phase to address:** P-FND (guard skeleton plus marker), extended in P-SUB and P-UI as each stub lands. Evidence in P-EVD.

---

### Pitfall 15: OIDC role and self-approval checks that are snapshots or compare the wrong identifiers

**What goes wrong:**
- **"Role checked at decision time" is not satisfied by `User.IsInRole` on the cookie.** Cookie claims are a login-time snapshot. The default cookie `ExpireTimeSpan` is 14 days with sliding expiration, so a reviewer removed from `role:case_supervisor` can keep approving.
- **Entra group claims** are object-ID GUIDs, not names. Above about 200 groups the token carries an *overage* indicator (`_claim_names` / `hasgroups`) instead of `groups`, so a membership check silently fails, or a careless null-check fails open.
- **Self-approval compares different identifier spaces.** `on_behalf_of` comes from popeye (a vocabulary user identifier). The reviewer is an Entra principal (`oid` + `tid`. Note that `sub` is **pairwise per application**, so it never matches anything external). A string compare of unrelated formats always reports "not self", so the check passes vacuously.
- **Tenant context in the UI.** A tenant ID carried in a hidden form field or query string, then used for `set_config`, is a tampering path around RLS.
- **Antiforgery.** Razor Pages validates antiforgery tokens on POST handlers automatically. A team that moves "decide" to a `MapPost` endpoint called by `fetch`, or adds `[IgnoreAntiforgeryToken]`, loses it. Minimal APIs validate only form-bound endpoints, and only with `UseAntiforgery`.
- **OIDC correlation and nonce cookies** need `SameSite=None; Secure`. On `http://localhost` the login loop fails in Chromium-based browsers, so use the HTTPS dev cert.

**How to avoid:**
- Prefer an Entra **app role** assigned to the group, which avoids overage. At the decide POST, re-check membership freshly: either a short cookie lifetime with `OnValidatePrincipal` re-validation, or a Microsoft Graph `checkMemberGroups` call. The Graph option needs a *read* credential. Confirm with the ratifier that a non-write directory-read credential is acceptable under REQ-ACT-04, or choose the short-lifetime option.
- Keep the role check behind `IReviewerAuthorization.CanDecide(reviewer, item, now)` and call it inside the decide transaction, not only in the page's `[Authorize]`.
- Define an explicit `ReviewerIdentity → vocabulary user id` mapping. If the mapping is missing or ambiguous, **refuse the decision** (fail closed). Test with a reviewer whose mapped id equals `on_behalf_of` → refused.
- Derive the tenant from the queue item loaded through the reviewer's authorised tenant set server-side, never from form input.
- Keep decide as a Razor Page POST handler. Add a test that a POST without the antiforgery token returns `400`.
- Use PRG (Post/Redirect/Get) so a double-click lands on a clean `409` "already decided" message.

**Phase to address:** P-UI. The identity-mapping question is an early ratifier question to raise in P-SUB planning.

---

### Pitfall 16: Testcontainers on Windows that test the wrong database or don't run at all

**What goes wrong:**
- **Tests silently hit the shared dev instance.** A developer's `.env` sets `OLIVE_POSTGRES_URL=...localhost:5432`, which `deploy-postgres-1` publishes, and a test reads config instead of the container's mapped port. Tests then mutate a database shared with Nexus, loan_engine and hometown, and cluster-wide roles can collide.
- Docker Desktop is in **Windows containers** mode, so `postgres` fails to pull or run. Docker Desktop isn't running, so you get an opaque `DockerUnavailableException`. WSL2 integration is off.
- **Ryuk** (the resource reaper) is blocked by corporate policy, so tests hang at startup or orphan containers after `TESTCONTAINERS_RYUK_DISABLED=true`.
- **Line endings.** Git here has `core.autocrlf=true` and the repo has **no `.gitattributes`**. A fresh Windows clone converts `contracts/vendored/vocabulary.yaml` to CRLF. Its SHA-256 then no longer matches `VENDORED.md` (`60a44f00…e8e7`; the current working copy happens to match). A migration runner that stores per-migration checksums will report "modified migration" between a Windows clone and Linux CI. JSON fixtures hashed as bytes (golden hash files) differ in the same way.
- One container per test class (`IAsyncLifetime`) makes the suite slow. Roles are cluster-wide, so per-class `CREATE ROLE` in a shared container collides under xUnit parallelism.
- The image isn't pinned, or is pinned to a different major version than `deploy-postgres-1`.
- CI on `windows-latest` GitHub runners can't run Linux containers.

**How to avoid:**
- Add `.gitattributes` **first**: `* text=auto eol=lf`, plus `*.sql`, `*.json`, `*.yaml`, `*.yml` and `*.md` with `eol=lf`, and fixtures and vendored files `-text` if they must be byte-exact. Renormalise once, and add a test that recomputes the vocabulary SHA-256 (CONCERNS already asks for this).
- The test fixture builds connection strings **only** from `container.Hostname` / `GetMappedPublicPort(5432)`. Add a guard that fails the test run if the connection's port is 5432 on localhost or the server version string matches the shared instance.
- Use one container per test assembly (xUnit v3 assembly fixture). Bootstrap the roles once, migrate a template database once, then `CREATE DATABASE test_x TEMPLATE olive_template` per test class for isolation.
- Pin `WithImage("postgres:<major>-alpine")` to the shared instance's major version.
- Run CI on `ubuntu-latest`. Document Docker Desktop's Linux-containers mode and WSL2 in the README, and add a preflight that skips Testcontainers tests with a clear message locally but **fails** in CI (`CI=true`).
- Note the Docker Desktop licence: paid subscription required above 250 employees or $10M revenue. Confirm TWG coverage *(MEDIUM: licence terms from training data)*.

**Phase to address:** P-FND.

---

## Moderate Pitfalls

### Expiry and clock handling
- Mixing clocks: DB `now()` for `created_at` (which is *transaction start* time) and the app clock for `expires_at`. Tests using `FakeTimeProvider` then disagree with the DB. **Fix:** inject `TimeProvider` everywhere (`Microsoft.Extensions.TimeProvider.Testing` → `FakeTimeProvider`), pass `@now` as a parameter into every time-sensitive SQL statement, and use no `now()` or `DEFAULT now()` in business logic.
- Npgsql 6+ throws on a `DateTime` with `Kind=Local/Unspecified` for `timestamptz`. Use `DateTimeOffset` from `TimeProvider.GetUtcNow()`.
- Compute expiry as `now + min(8h default, catalog max, expires_in_seconds?)`, capped by `key.expires_at`. Reject `expires_in_seconds ≤ 0` or above the max with a `400`, never a silent clamp, which is a mutation. Decide what happens when `key.expires_at` is already ≤ now (probably `401`).
- **Who writes `expired`?** Lazy expiry on an approve attempt must **commit** the `expired` transition, the event and the queue `withdrawn` (with reason) even though the request returns an error. A rollback-on-error unit of work will undo it. The queue list must hide or flag expired items even when nobody has touched them. Check `expires_at > @now` in the list query.
- Clock skew between popeye's `expires_at` and olive's clock. Use a small fixed margin on token caching only, not on proposal expiry, which is olive's own clock.
- **Phase:** P-LIFE (and P-SUB for the cap computation).

### Reference-only queue payloads
- `subject_ref` / `payload_ref` are free strings, so a body can be smuggled in. **Fix:** validate against a URI-scheme allowlist (`proposal:`, IR-1's bluto scheme) with a max length. Enforce it with a DB `CHECK` (regex plus length) as well as in the domain. Store the proposal's own fields in `proposal`, never copied into `queue_item`.
- The IR-1 create-body rule: `tenant_id`, `producer`, `item_id` and `state` in the body → `400 server_assigned_field`. The scaffold's queue-item schema *requires* `tenant_id`, which is the opposite.
- **Phase:** P-LIFE.

### Catalog loading
- The hand-rolled YAML regex matches the wrong lists (CONCERNS). Use YamlDotNet into a typed model, validate against `action-type.schema.json`, cross-check filename = `action_id`, fail startup on duplicates, and **never** expose `Catalog.Load(anyDir)` outside composition and tests. Remove `AddAtRuntime` and replace its test with a reflection test that finds no public mutator.
- **Phase:** P-CON.

### Entitlement intersection fail-open
- `null` grants currently mean allow. Entitlements must come only from introspection and the `on_behalf_of` user's entitlements, never from the body. `null` or empty → deny. Test that an agent with entitlement X on behalf of a user without X is refused, and the reverse.
- **Phase:** P-SUB.

### Alarms that are just log lines
- §16.2 / §17.3 require an "operational alarm" for hometown or popeye `401`/`403` and token mismatch. A `LogWarning` can't be asserted in tests and nobody sees it. **Fix:** an `IAlarmSink` interface (log plus metric counter) that tests assert was called exactly once per case.
- **Phase:** P-SUB.

---

## Minor Pitfalls

- **Stringly-typed states** (CONCERNS). Use enums or closed types, and map them to Postgres `CHECK` constraints that a test compares against the enum values.
- **Test root resolution** via `../../../../..`. Walk up to `Olive.sln` instead (P-FND).
- **Stale spec.** `specs/DECISION_..._v0.1.md` sits beside v0.5. Mark it superseded so the implementer doesn't read §15 from v0.1 (P-FND).
- **`global.json` pin** `10.0.204` with `latestFeature`. Make CI install the same SDK.
- **Migration location mismatch**: `db/migrations/V001__…` against Decision §13's `sql/migrations/0001_init.sql`. Decide once in P-DB. Because migrations are forward-only, renaming after the first applied run means a runner-level rename mapping.
- **Big-integer or Unicode edge cases in `run_id` / `trace_id`.** Treat them as opaque strings, compare ordinally, and never `ToLower()`.

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Bind the proposal to a DTO with default `[FromBody]` | Less code | Hash mismatches, coercion, stripped keys (§17.2 violation) | Never on `POST /v1/proposals` |
| Tests on the container superuser | Fast setup | RLS untested, so REQ-QUE-02 evidence is void | Only for the one-time role bootstrap step |
| `GRANT ALL` to `olive_app` | Fewer migration lines | Append-only ledger is mutable | Never |
| SERIALIZABLE everywhere "for safety" | Feels safe | `40001` failures without retry → 500s | Only with a tested retry wrapper |
| In-memory rate limiter | Trivial | Not shared across instances, ignores fake clock, burns on retries | Unit tests only |
| `User.IsInRole` from the cookie for approvals | Built in | Revoked supervisors keep approving for up to 14 days | Local stub IdP only |
| Lazy expiry with no list-query filter | Simple | Expired items shown as approvable | Never |
| Fixture files copied to the publish output | Convenient | Fixture data available in prod images | Only if the startup guard is in place and tested |

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| popeye introspection (stub) | Caching introspection results; passing the key in a URL | Introspect on every request. Key only in the body or header. Stub records every call for the §17.4 capture |
| popeye token issuance (stub) | Trusting returned claims; cache keyed without `key_id` | Assert `delegated_agent_id` / `tenant_id` equal introspection before caching; key `(tenant, agent, key_id)` |
| hometown resolver (fixture) | Fixture shape drifts from the 7-field §17.3 response | Validate fixtures against a committed response schema; include `schema_version`, null `run_id`/`agent_id`/`proposal_hash` cases |
| hometown proposal hash | Each side "implements JCS" independently | Shared golden-hash fixture set run in both CIs |
| Entra OIDC | Using `sub` or the `groups` claim | `oid` + `tid`; app roles; fresh check at decide time |
| Shared Postgres (`deploy-postgres-1`) | Roles in migrations; tests pointed at 5432 | Separate bootstrap script; Testcontainers mapped ports only |
| Npgsql | Many data sources, `TransactionScope`, `No Reset On Close` | One `NpgsqlDataSource`, explicit transactions, default reset |

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| DB transaction held across resolver or popeye calls | `idle in transaction`, pool exhaustion, timeouts | Short transactions around step 3 and step 7 only | A handful of concurrent slow resolver calls (pool default 100) |
| Missing indexes for queue list or poll (`(tenant_id, state, kind)`, `(tenant_id, decided_at)`) | Slow UI list | Add with the IR-1 tables | Irrelevant at POC scale; add anyway since cheap |
| Rate-limit count without index on `(tenant_id, agent_id, action_type, created_at)` | Slow submissions | Index | Thousands of proposals |
| A Testcontainers container per test class | CI taking minutes | Assembly fixture plus template DBs | More than about 5 test classes |

## Security Mistakes

| Mistake | Risk | Prevention |
|---------|------|------------|
| Tenant taken from the request body or a UI form field | Cross-tenant write or read | Tenant only from introspection (API) or the server-side reviewer context (UI) |
| Self-approval check across mismatched identifier spaces | Proposer approves own action | Explicit identity mapping; fail closed if unmapped |
| Stub IdP or popeye stub registered in production | Anyone approves, or anyone is Liam | Marker interface plus pre-`Run` guard covering *all* stubs |
| Negative caching of `404` traces | Permanent refusal from a timing gap | Cache only `200` |
| Single-column FKs under RLS | Cross-tenant references | Composite `(tenant_id, id)` FKs |
| Weak no-connector test (substring `HttpClient` in one file) | A write path appears unnoticed | Arch test over all of `src/` plus `PackageReference` allowlist |

## UX Pitfalls

| Pitfall | User Impact | Better Approach |
|---------|-------------|-----------------|
| Double-submit on decide | Confusing error page | PRG pattern; clear "already decided by X at T" |
| Expired items still listed as actionable | Reviewer writes a rationale, then gets an error | Filter or flag by `expires_at` in the list; show time remaining |
| Showing raw parameter bodies with no provenance | Reviewer can't tell fixture-verified from real | Show `trace_verification` and the reviewer-auth source prominently (Phase 1 says "fixture") |
| Rationale accepted when whitespace-only | Meaningless audit | Reject blank-after-trim (rationale isn't hashed, so trimming for the *check* is fine; store as entered) |

## "Looks Done But Isn't" Checklist

- [ ] **RLS:** test connects as `olive_app` (asserted `rolsuper = false`, `rolbypassrls = false`), seeds two tenants, and asserts that the own-tenant count is above 0.
- [ ] **Unset tenant:** errors on both a fresh and a *reused pooled* connection.
- [ ] **Append-only:** `UPDATE`, `DELETE` and `TRUNCATE` on `execution_ledger` and `proposal_event` as `olive_app` → `42501`.
- [ ] **Ownership:** all objects are owned by `olive_owner`.
- [ ] **Non-mutating validation:** `"5"` → 400; unknown key in `parameters` preserved in the hash; duplicate key → 400; `RUN_ID` casing not accepted.
- [ ] **JCS:** passes RFC 8785 vectors *and* the hometown golden set.
- [ ] **Format validation:** a bad `uuid` fails the schema.
- [ ] **Idempotency race:** N parallel identical first submissions → one row, one queue item, all `200`.
- [ ] **Outcome matrix:** each row of the Pitfall 9 table has a "call twice" test.
- [ ] **State races:** parallel approve ∥ approve and approve ∥ reject → exactly one wins.
- [ ] **Expiry:** approve and execute after expiry → distinct error, `expired` committed, queue item withdrawn.
- [ ] **Execution:** fault-injected retry → one target write, first result returned; `execution_failed` path exercised.
- [ ] **Leak test:** sentinel key and token, Trace-level logs, grep of logs, JSONL and all table rows.
- [ ] **Outbound capture:** no `new HttpClient(` in `src/`; key only on popeye calls.
- [ ] **Startup guard:** `production` with *each* stub → exit non-zero; `OLIVE_ENVIRONMENT` unset → exit non-zero; tested with no test overrides.
- [ ] **Decide:** antiforgery enforced; role re-checked at decision time; self-approval refused via mapped identity.
- [ ] **Vendored hash:** SHA-256 test passes on a fresh Windows clone (`.gitattributes` in place).

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Objects owned by the wrong role | MEDIUM | New forward migration with `ALTER TABLE ... OWNER TO olive_owner` for each object, then re-apply FORCE RLS and default privileges |
| JCS mismatch found after fixtures are generated | MEDIUM | Fix the canonicalizer, regenerate the shared golden set with hometown, re-run the §17.4 suite; already-stored `trace_proposal_mismatch` rows stay (append-only) and need an explanatory event |
| Token or key found in logs | HIGH | Treat as compromised: revoke at popeye (stub now, real later), purge logs, add the sentinel test that would have caught it |
| Tests were superuser all along | MEDIUM | Switch the fixture to role strings; expect newly failing tests that reveal real RLS gaps |
| Stored refusals caused by a wrong outcome matrix | LOW-MEDIUM | Callers need a new `run_id`. Fix the matrix; stored rows are history, so don't delete them |

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| 1 Superuser or owner RLS bypass | P-FND, P-DB | `rolsuper/rolbypassrls` assertion; two-tenant layer-removal tests |
| 2 Object ownership | P-DB | `relowner` test |
| 3 `SET LOCAL` / pooling / GUC `''` | P-DB | `Max Pool Size=1` cross-tenant and unset-tenant tests |
| 4 Policies and grants / RETURNING / FKs / views | P-DB, P-LIFE | Privilege-matrix test; `42501` on mutation |
| 5 Raw-body mutation | P-CON | §17.4 `"5"` and unknown-key tests; duplicate-key test |
| 6 JCS correctness | P-CON (before P-SUB) | RFC vectors plus hometown golden set |
| 7 Schema validator config | P-CON | Bad-`uuid` test; examples suite |
| 8 Idempotency race | P-SUB | Parallel-submit test |
| 9 Stored or not-stored matrix and rate limiting | P-SUB | Parameterised call-twice tests |
| 10 State-machine races | P-LIFE | Parallel transition tests; mapping-invariant test |
| 11 Exactly-once execution | P-LIFE | Fault-injection test |
| 12 Secret leakage | P-FND, P-SUB, P-EVD | Sentinel grep at Trace; outbound capture |
| 13 Token cache | P-SUB | §17.4 token rows |
| 14 Stub leakage / startup guard | P-FND → P-UI, P-EVD | Process-level guard tests |
| 15 OIDC role and self-approval | P-UI | Revoked-role, self-approval and antiforgery tests |
| 16 Testcontainers / Windows / CRLF | P-FND | Fresh-clone hash test; port guard; CI on Ubuntu |

## Open Questions to Raise with the Ratifier (found while researching)

1. **Malformed body under an existing `run_id`:** `400` or `409`? Step 3 precedes step 4, but the hash is defined "after schema validation" (§15.2 / §15.3).
2. **Reviewer-to-`on_behalf_of` identity mapping:** which identifier space is `on_behalf_of`, and how does an Entra `oid` map to it?
3. **Fresh role check at decision time:** is a directory-read credential (Graph) acceptable under REQ-ACT-04, or should it be a short cookie lifetime with re-validation?
4. **Is `execution_failed` terminal?** Does a retry return the failure (REQ-ACT-03 "first result") or re-execute?
5. **IR-1:** must the decider be the claimant? How are abandoned claims released?
6. **Order of the unknown-action refusal against parameter-schema `400`** inside §15.2 step 4.

## Sources

- PostgreSQL docs: Row Security Policies (`FORCE`, superuser and `BYPASSRLS` exemption, per-command policies), `INSERT` (RETURNING / ON CONFLICT privileges), `CREATE VIEW` (`security_invoker`, PG15). HIGH for the RLS exemption rules, MEDIUM for the ON CONFLICT DO NOTHING privilege detail.
- PostgreSQL mailing lists on custom GUCs reading back as `''` after `SET LOCAL` / `set_config(..., true)`: [pgsql-bugs: transaction-local custom settings set to ''](https://www.postgresql.org/message-id/CA%2BQ86ij0KDCB0G45G509-8q0DNR611gcKG-sSM83GA1EBL7boA%40mail.gmail.com), [SET LOCAL doesn't become undefined (Tom Lane)](https://postgresql.org/message-id/745.1584540983%40sss.pgh.pa.us), [set_config is_local escapes transaction boundaries](https://www.postgresql.org/message-id/997A11B3-BC14-44FB-A505-BE7C49BE5330%40subzero.cloud). MEDIUM-HIGH.
- Npgsql pooled-connection reset (`DISCARD ALL` or equivalent; TransactionScope edge case): [npgsql/npgsql#4451](https://github.com/npgsql/npgsql/issues/4451), [session parameters with Npgsql](https://startdebugging.net/2026/10/how-to-set-postgresql-session-parameters-on-every-ef-core-connection-with-npgsql/). MEDIUM.
- System.Text.Json duplicate properties (.NET 10 `AllowDuplicateProperties`, default true; `JsonSerializerOptions.Strict`): [JsonSerializerOptions.AllowDuplicateProperties](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.allowduplicateproperties), [dotnet/docs commit](https://github.com/dotnet/docs/commit/5af705fa0d330cb330bc772c75f6a71afc62fe72), [Duende: harden .NET JSON deserialization](https://duendesoftware.com/blog/20260430-harden-your-dotnet-json-deserialization). HIGH for the serializer; MEDIUM for `JsonDocumentOptions` availability.
- ASP.NET Core Web JSON defaults (`AllowReadingFromString`, case-insensitive): [breaking change: numbers as strings](https://learn.microsoft.com/is-is/dotnet/core/compatibility/serialization/5.0/jsonserializer-allows-reading-numbers-as-strings), [configure options](https://learn.microsoft.com/lb-lu/dotNET/standard/serialization/system-text-json/configure-options). HIGH.
- JsonSchema.Net `RequireFormatValidation` default false: [EvaluationOptions](https://docs.json-everything.net/api/JsonSchema.Net/EvaluationOptions/). HIGH.
- HttpClientFactory header redaction default (.NET 9+): [Breaking change: redact headers](https://learn.microsoft.com/en-ca/dotnet/Core/compatibility/networking/9.0/redact-headers). HIGH.
- Testcontainers .NET usage with explicit image: [Docker guide](https://docs.docker.com/guides/testcontainers-dotnet-getting-started/), [modules](https://dotnet.testcontainers.org/modules/). MEDIUM. (A claim that the v4 parameterless `PostgreSqlBuilder()` is obsolete was **not** confirmed; pin the image regardless.)
- RFC 8785 (JCS) number and string serialization and UTF-16 key sort; cyberphone reference implementations. HIGH for the RFC rules (training data, stable standard); MEDIUM for the .NET package availability.
- Entra ID group overage, `oid` vs pairwise `sub`, app roles; Razor Pages automatic antiforgery; ASP.NET Core cookie default 14-day sliding lifetime: training data, MEDIUM. Verify in P-UI research.
- Repository evidence: `.planning/codebase/CONCERNS.md`; `git config core.autocrlf` = `true` with no `.gitattributes` (checked 2026-10-21); `specs/DECISION_olive-poc2-phase1_v0.5.md` §13–§17.

---
*Pitfalls research for: Olive governed action broker (Phase 1, simulation-only)*
*Researched: 2026-10-21*
