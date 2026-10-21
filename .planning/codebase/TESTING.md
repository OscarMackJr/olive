# Testing Patterns

**Analysis Date:** 2026-10-21

## Test Framework

**Runner:**
- xUnit v3 (`xunit.v3` 3.2.0) with `xunit.runner.visualstudio` 3.1.5 and `Microsoft.NET.Test.Sdk` 18.0.0
- Target `net10.0`; SDK pinned in `global.json` (10.0.204, rollForward latestFeature)
- Config: `tests/Olive.Tests/Olive.Tests.csproj` (`UseAppHost=true` because xUnit v3 tests are executables)

**Assertion Library:**
- xUnit `Assert` (global using in `tests/Olive.Tests/GlobalUsings.cs`)

**Run Commands:**
```bash
dotnet test Olive.sln                 # Run all tests
dotnet test Olive.sln --filter "FullyQualifiedName~REQ_ACT"   # Subset by requirement prefix
dotnet test Olive.sln --collect:"XPlat Code Coverage"         # Coverage (no collector package referenced; may need adding)
```

## Test File Organization

**Location:**
- Separate project: `tests/Olive.Tests/`. Single file `RequirementsTests.cs`.

**Naming:**
- Class `RequirementsTests` (sealed). Test names are `REQ_<AREA>_<nn>_<snake_case_behavior>` mapped to charter requirements, or plain `snake_case_behavior` for cross-cutting properties.

**Structure:**
```
tests/Olive.Tests/
├── GlobalUsings.cs
├── Olive.Tests.csproj
└── RequirementsTests.cs
```

## Test Structure

**Suite Organization:**
```csharp
public sealed class RequirementsTests
{
    private static readonly Guid Tenant = Guid.Parse("5325ffe9-...");
    private static readonly Guid OtherTenant = Guid.Parse("f92c9dba-...");
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static ActionBroker Broker(ReviewQueueService? queue = null, string? output = null) { ... }

    [Fact] public void REQ_ACT_02_policy_refusal_happens_before_queueing()
    {
        var q = new ReviewQueueService();
        var p = Broker(q).Submit(Proposal("marketing"), DateTimeOffset.UtcNow);
        Assert.Equal(Outcomes.RefusedPolicy, p.State);
        Assert.Empty(q.List(Tenant));
    }
}
```

**Patterns:**
- Each test builds its own objects (no shared mutable state, no setup/teardown).
- Helpers `Broker(...)` and `Proposal(...)` create a real `ActionBroker` wired to the real `Catalog.Load` of `catalog/` and `contracts/vendored/vocabulary.yaml`, resolved through `Root`.
- Time is injected as `DateTimeOffset` arguments; expiry tests use `UtcNow.AddMinutes(-1)`.
- Exceptions tested with `Assert.Throws<T>(...)`.
- Tests are one-liners-per-method in the existing file; new tests may be multi-line for readability.

## Mocking

**Framework:** None. No mocking library.

**Patterns:**
- Use real in-memory implementations (`ReviewQueueService`, `PolicyEngine`, `SimulatedActionExecutor`) with a temp JSONL path: `Path.Combine(Path.GetTempPath(), $"olive-{Guid.NewGuid()}.jsonl")`.
- The seam `ISimulatedActionExecutor` exists if a fake is ever needed.

**What to Mock:**
- Nothing external exists to mock. Do not introduce HTTP/SDK fakes; a real connector is out of scope (`AGENTS.md`).

**What NOT to Mock:**
- Catalog, policy engine, queue: use the real ones.

## Fixtures and Factories

**Test Data:**
- Fixed tenant GUIDs as static readonly fields; `Proposal(purpose, expires)` factory helper in `RequirementsTests.cs`.
- JSON contract fixtures: `contracts/examples/*.valid.json` and `*.invalid.json`, `catalog/crm.case_note@1.yaml`.

**Location:**
- `contracts/examples/` (shared contract fixtures), helpers inside the test class.

## Coverage

**Requirements:** None enforced; no coverage tooling configured.

## Test Types

**Unit/Behavioral Tests:**
- Requirement-level tests over the in-memory domain: approval flow (`REQ_ACT_01`), policy refusal before queueing (`02`), idempotent execution (`03`), expiry (`05`), runtime catalog rejection (`06`), decisions need who/when/why (`REQ_QUE_03`), tenant scoping (`REQ_QUE_02`), shared Bluto queue kind (`REQ_QUE_04`).

**Source/Contract Guard Tests:**
- `REQ_ACT_04` reads `src/Olive/Domain.cs` and asserts there is no `HttpClient` and `SimulatedActionExecutor` is present. Keep this passing.
- `REQ_QUE_01` reads `contracts/schemas/queue-item.schema.json` and the invalid example to confirm reference-only payloads.

**Integration Tests:**
- Not present. `db/migrations/V001__action_broker.sql` (RLS) is not executed by any test; `ListWithDatabaseRls` in `src/Olive/Domain.cs` only mirrors RLS in memory.

**E2E Tests:**
- Not used.

## Common Patterns

**Async Testing:**
- Not applicable; the domain is synchronous.

**Error Testing:**
```csharp
Assert.Throws<InvalidOperationException>(() => b.Execute(Tenant, p.ProposalId, DateTimeOffset.UtcNow));
Assert.Throws<ArgumentException>(() => q.Decide(Tenant, i.ItemId, "", Outcomes.Approved, "", DateTimeOffset.UtcNow));
```

**Gaps to be aware of:**
- No tests for cross-tenant `Get` (`KeyNotFoundException`), rate-limit exhaustion, agent delegation intersection, catalog parse failures, or schema validation beyond property-name checks (no JSON Schema validator package).
- Tests depend on the repo layout via `Root` (five levels up from `bin/<config>/<tfm>`); keep the directory structure stable.

---

*Testing analysis: 2026-10-21*
