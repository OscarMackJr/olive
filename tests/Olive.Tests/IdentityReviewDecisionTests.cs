using System.Net;
using System.Text.Json;
using Npgsql;
using Olive.Application;
using Olive.Catalog;
using Olive.Persistence;

namespace Olive.Tests;

// IRQ-02 / TC-IRQ-04 / TC-IRQ-05 / TC-IRQ-06: reviewer decisions on identity_review items (accept, reject, defer).
[Trait("Category", "Integration")]
public sealed class IdentityReviewDecisionTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private const string Sentinel = "IRQ-RATIONALE-SENTINEL-4471";
    private const string Steward = "role:identity_steward";
    private static readonly Guid Alpha = TestPaths.TenantAlpha;
    private static readonly Guid Beta = TestPaths.TenantBeta;
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- service level (recording fakes) ----

    private sealed class RecordingStore(QueueItemDetail? item) : IQueueStore
    {
        public int IdentityCalls { get; private set; }
        public int ApprovalCalls { get; private set; }
        public int PayloadCalls { get; private set; }
        public string? LastDecision { get; private set; }
        public DecideResult Outcome { get; set; } = new DecideResult.NotFound();

        public Task<IReadOnlyList<QueueItemSummary>> ListAsync(Guid tenantId, string? kind, string? state, DateTimeOffset now, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueueItemDetail?> GetAsync(Guid tenantId, Guid itemId, CancellationToken ct) => Task.FromResult(item);
        public Task<IReadOnlyList<QueueItemEventRecord>?> EventsAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewerPayload?> GetPayloadAsync(Guid tenantId, Guid itemId, CancellationToken ct)
        {
            PayloadCalls++;
            return Task.FromResult<ReviewerPayload?>(null);
        }

        public Task<ClaimResult> ClaimAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReleaseResult> ReleaseAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<CreateIdentityResult> CreateIdentityItemAsync(Guid tenantId, string producer, string kind, string subtype, Guid producerRef, string subjectRef, string payloadRef, string rulesetVersion, string bodySha256, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();

        public Task<DecideResult> DecideAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, bool approveIdentityVerified, DateTimeOffset now, CancellationToken ct)
        {
            ApprovalCalls++;
            return Task.FromResult(Outcome);
        }

        public Task<DecideResult> DecideIdentityAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, DateTimeOffset now, CancellationToken ct)
        {
            IdentityCalls++;
            LastDecision = decision;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class NeverResolver : IUserIdentityResolver
    {
        public Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) => throw new InvalidOperationException("identity check must not run");
    }

    private static QueueItemDetail Detail(string kind, string state = "claimed", string claimedBy = "rev-1") =>
        new(Alpha, Guid.NewGuid(), kind, kind == "action_approval" ? "proposal" : "candidate_pair", kind == "action_approval" ? "olive" : "bluto", Guid.NewGuid(), state, "s", "p", claimedBy, Now, null,
            null, null, null, null, null, kind == "action_approval" ? Guid.NewGuid() : null);

    private static (QueueService Service, RecordingStore Store) Build(QueueItemDetail? item)
    {
        var store = new RecordingStore(item);
        var resolver = new RecordingUserIdentityResolver(new NeverResolver(), [], []);
        var check = new ApprovalIdentityCheck(new UserIdentityVerifier(resolver, new UserIdentityVerifierOptions()));
        return (new QueueService(store, QueueKindRegistry.LoadEmbedded(), check, new ManualTimeProvider(Now)), store);
    }

    private static ReviewerContext Reviewer(params string[] groups) => new(Alpha, "rev-1", "tid-1", groups, "stub");

    [Theory]
    [InlineData("accept")]
    [InlineData("reject")]
    [InlineData("defer")]
    public async Task IRQ_05_invalid_decision_or_rationale_is_invalid_request_and_never_reaches_the_store(string decision)
    {
        var (service, store) = Build(Detail("identity_review"));
        foreach (var rationale in new string?[] { null, "", "   ", "　", new string('x', 4001), "a\0b" })
        {
            Assert.IsType<DecisionResult.InvalidRequest>(await service.DecideAsync(Reviewer(Steward), Guid.NewGuid(), decision, rationale, Ct));
        }

        foreach (var bad in new string?[] { null, "", " ", "approve", "ACCEPT" })
        {
            Assert.IsType<DecisionResult.InvalidRequest>(await service.DecideAsync(Reviewer(Steward), Guid.NewGuid(), bad, "ok", Ct));
        }

        Assert.Equal(0, store.IdentityCalls);
    }

    [Fact]
    public async Task IRQ_04_role_comes_from_the_registry_for_each_kind_before_any_lookup()
    {
        var (identity, identityStore) = Build(Detail("identity_review"));
        Assert.IsType<DecisionResult.RoleNotPermitted>(await identity.DecideAsync(Reviewer("role:case_supervisor"), Guid.NewGuid(), "accept", "ok", Ct));

        var (approval, approvalStore) = Build(Detail("action_approval"));
        Assert.IsType<DecisionResult.RoleNotPermitted>(await approval.DecideAsync(Reviewer(Steward), Guid.NewGuid(), "approve", "ok", Ct));

        Assert.Equal(0, identityStore.IdentityCalls + identityStore.PayloadCalls + approvalStore.ApprovalCalls + approvalStore.PayloadCalls);
    }

    [Fact]
    public async Task IRQ_02_identity_decide_never_runs_the_identity_check_or_reads_a_payload()
    {
        var (service, store) = Build(Detail("identity_review"));
        store.Outcome = new DecideResult.Decided(Detail("identity_review", "decided"));

        var result = await service.DecideAsync(Reviewer(Steward), Guid.NewGuid(), "accept", "ok", Ct);

        Assert.IsType<DecisionResult.Decided>(result);
        Assert.Equal(1, store.IdentityCalls);
        Assert.Equal("accept", store.LastDecision);
        Assert.Equal(0, store.PayloadCalls);
        Assert.Equal(0, store.ApprovalCalls);
        Assert.Null(((DecisionResult.Decided)result).Item.ProposalId);
    }

    [Fact]
    public async Task IRQ_06_service_maps_deferred_and_withdrawn()
    {
        var (service, store) = Build(Detail("identity_review"));
        store.Outcome = new DecideResult.Deferred(Detail("identity_review", "open", null!));
        Assert.IsType<DecisionResult.Deferred>(await service.DecideAsync(Reviewer(Steward), Guid.NewGuid(), "defer", "ok", Ct));
        store.Outcome = new DecideResult.Withdrawn();
        Assert.IsType<DecisionResult.Withdrawn>(await service.DecideAsync(Reviewer(Steward), Guid.NewGuid(), "accept", "ok", Ct));
    }

    // ---- store level ----

    private static PostgresQueueStore Store(TestDatabase db, TimeProvider? time = null) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)), time);

    private static Task<T?> Scalar<T>(TestDatabase db, string sql, params (string, object)[] args) => QueueClaimTests.Scalar<T>(db, Alpha, sql, args);

    private static Task<DecideResult> Identity(PostgresQueueStore store, Guid item, string actor, string decision, string rationale = Sentinel) =>
        store.DecideIdentityAsync(Alpha, item, actor, "stub", decision, rationale, Now, Ct);

    [Fact]
    public async Task IRQ_06_defer_returns_to_open_with_decision_columns_null_and_one_deferred_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct));

        var deferred = Assert.IsType<DecideResult.Deferred>(await Identity(store, item, "stew-1", "defer", "need more evidence"));

        Assert.Equal("open", deferred.Item.State);
        Assert.Null(deferred.Item.ClaimedBy);
        Assert.Null(deferred.Item.Decision);
        Assert.Null(deferred.Item.DecidedBy);
        Assert.Null(deferred.Item.DecidedAt);
        Assert.Null(await Scalar<string>(db, "select rationale from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i and event_type = 'deferred' and from_state = 'claimed' and to_state = 'open' and actor = 'stew-1'", ("i", item)));
        Assert.Equal("defer", await Scalar<string>(db, "select detail->>'decision' from queue_item_event where item_id = @i and event_type = 'deferred'", ("i", item)));
        Assert.Equal("need more evidence", await Scalar<string>(db, "select detail->>'rationale' from queue_item_event where item_id = @i and event_type = 'deferred'", ("i", item)));
    }

    [Fact]
    public async Task IRQ_06_same_steward_can_reclaim_after_defer_and_a_second_steward_can_claim_too()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct));
        Assert.IsType<DecideResult.Deferred>(await Identity(store, item, "stew-1", "defer"));

        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct));
        Assert.IsType<DecideResult.Deferred>(await Identity(store, item, "stew-1", "defer"));
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "stew-2", Now, Ct));
    }

    [Fact]
    public async Task IRQ_06_claim_defer_reclaim_accept_records_the_full_event_history_without_a_rationale_on_decided()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var decidedAt = Now.AddHours(3);
        var store = Store(db, new ManualTimeProvider(decidedAt));
        await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct);
        await Identity(store, item, "stew-1", "defer");
        await store.ClaimAsync(Alpha, item, "stew-2", Now, Ct);

        var decided = Assert.IsType<DecideResult.Decided>(await Identity(store, item, "stew-2", "accept"));

        Assert.Equal("decided", decided.Item.State);
        Assert.Equal("accept", decided.Item.Decision);
        Assert.Equal("stew-2", decided.Item.DecidedBy);
        Assert.Equal(decidedAt, decided.Item.DecidedAt);
        Assert.Equal(Sentinel, await Scalar<string>(db, "select rationale from queue_item where item_id = @i", ("i", item)));
        var events = await store.EventsAsync(Alpha, item, Ct);
        Assert.Equal(["claimed", "deferred", "claimed", "decided"], events!.Select(e => e.EventType).ToArray());
        var decidedEvent = events![^1];
        Assert.DoesNotContain(Sentinel, decidedEvent.DetailJson ?? "");
        Assert.DoesNotContain("rationale", decidedEvent.DetailJson ?? "");
        Assert.Equal(0L, await Scalar<long>(db, "select count(*) from proposal_event"));
    }

    [Fact]
    public async Task IRQ_02_reject_decides_the_item_and_touches_no_proposal()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);
        await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct);

        var decided = Assert.IsType<DecideResult.Decided>(await Identity(store, item, "stew-1", "reject"));

        Assert.Equal("reject", decided.Item.Decision);
        Assert.Equal(0L, await Scalar<long>(db, "select count(*) from proposal"));
    }

    [Fact]
    public async Task IRQ_06_claimant_rules_idempotency_and_conflict()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);

        Assert.IsType<DecideResult.NotClaimed>(await Identity(store, item, "stew-1", "accept"));
        Assert.IsType<DecideResult.NotClaimed>(await Identity(store, item, "stew-1", "defer"));
        await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct);
        Assert.IsType<DecideResult.NotClaimant>(await Identity(store, item, "stew-2", "accept"));
        Assert.IsType<DecideResult.NotClaimant>(await Identity(store, item, "stew-2", "defer"));
        Assert.IsType<DecideResult.NotFound>(await store.DecideIdentityAsync(Beta, item, "stew-1", "stub", "accept", "ok", Now, Ct));

        Assert.IsType<DecideResult.Decided>(await Identity(store, item, "stew-1", "accept"));
        Assert.IsType<DecideResult.AlreadyDecidedSame>(await Identity(store, item, "stew-1", "accept"));
        Assert.IsType<DecideResult.Conflict>(await Identity(store, item, "stew-1", "reject"));
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i and event_type = 'decided'", ("i", item)));
    }

    [Fact]
    public async Task IRQ_06_argument_guards()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);
        await Assert.ThrowsAsync<ArgumentException>(() => Identity(store, item, " ", "accept"));
        await Assert.ThrowsAsync<ArgumentException>(() => Identity(store, item, "stew-1", "accept", "  "));
        await Assert.ThrowsAsync<ArgumentException>(() => Identity(store, item, "stew-1", "approve"));
    }

    [Fact]
    public async Task IRQ_09_withdrawn_identity_item_gives_a_withdrawn_result_for_decide_and_release()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid(), "withdrawn", withdrawnReason: "superseded");
        var store = Store(db);

        Assert.IsType<DecideResult.Withdrawn>(await Identity(store, item, "stew-1", "accept"));
        Assert.IsType<DecideResult.Withdrawn>(await Identity(store, item, "stew-1", "defer"));
        Assert.IsType<ReleaseResult.Withdrawn>(await store.ReleaseAsync(Alpha, item, "stew-1", Now, Ct));
    }

    // ---- HTTP ----

    private static async Task<HttpResponseMessage> Post(HttpClient c, Guid item, string path, string? json = null) =>
        await c.PostAsync($"{Items}/{item}/{path}", json is null ? null : ApiProposalTests.Json(json), Ct);

    private static string Decision(string decision, string? rationale = Sentinel) =>
        JsonSerializer.Serialize(rationale is null ? new Dictionary<string, string> { ["decision"] = decision } : new Dictionary<string, string> { ["decision"] = decision, ["rationale"] = rationale });

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task IRQ_06_steward_defers_a_second_steward_claims_and_accepts_with_full_history()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        using var s1 = factory.CreateReviewerClient("alpha-steward-1");
        using var s2 = factory.CreateReviewerClient("alpha-steward-2");

        Assert.Equal(HttpStatusCode.OK, (await Post(s1, item, "claim")).StatusCode);
        var defer = await Post(s1, item, "decision", Decision("defer"));
        Assert.Equal(HttpStatusCode.OK, defer.StatusCode);
        var deferred = await Json(defer);
        Assert.Equal("open", deferred.GetProperty("state").GetString());
        Assert.False(deferred.TryGetProperty("claimed_by", out _));
        Assert.False(deferred.TryGetProperty("decision", out _));

        Assert.Equal(HttpStatusCode.OK, (await Post(s2, item, "claim")).StatusCode);
        var accept = await Post(s2, item, "decision", Decision("accept"));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var decided = await Json(accept);
        Assert.Equal("decided", decided.GetProperty("state").GetString());
        Assert.Equal("accept", decided.GetProperty("decision").GetString());

        var events = await s1.GetAsync($"{Items}/{item}/events", Ct);
        var types = (await Json(events)).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("event_type").GetString()).ToArray();
        Assert.Equal(["claimed", "deferred", "claimed", "decided"], types);
        Assert.Equal(0L, await db.AdminCountAsync("select count(*) from execution_ledger"));
    }

    [Fact]
    public async Task IRQ_04_wrong_role_pairings_and_cross_tenant_are_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var identityItem = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var (approvalItem, _) = await DecisionHttp.SubmitAsync(factory, db, "irq04");
        using var sup = factory.CreateReviewerClient("alpha-sup-1");
        using var steward = factory.CreateReviewerClient("alpha-steward-1");
        using var betaSteward = factory.CreateReviewerClient("beta-steward");

        await Post(sup, identityItem, "claim");
        var supOnIdentity = await Post(sup, identityItem, "decision", Decision("accept"));
        Assert.Equal(HttpStatusCode.Forbidden, supOnIdentity.StatusCode);
        Assert.Equal("role_not_permitted", await DecisionHttp.ReasonOf(supOnIdentity));

        await Post(steward, approvalItem, "claim");
        var stewardOnApproval = await Post(steward, approvalItem, "decision", Decision("approve"));
        Assert.Equal(HttpStatusCode.Forbidden, stewardOnApproval.StatusCode);
        Assert.Equal("role_not_permitted", await DecisionHttp.ReasonOf(stewardOnApproval));

        Assert.Equal(HttpStatusCode.NotFound, (await Post(betaSteward, identityItem, "claim")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(betaSteward, identityItem, "decision", Decision("accept"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await betaSteward.GetAsync($"{Items}/{identityItem}", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("accept", null)]
    [InlineData("reject", null)]
    [InlineData("defer", null)]
    [InlineData("accept", "  ")]
    [InlineData("reject", "")]
    [InlineData("defer", "　")]
    [InlineData("approve", "ok")]
    public async Task IRQ_05_missing_rationale_or_foreign_decision_is_400(string decision, string? rationale)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        using var steward = factory.CreateReviewerClient("alpha-steward-1");
        await Post(steward, item, "claim");

        var response = await Post(steward, item, "decision", Decision(decision, rationale));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await DecisionHttp.ReasonOf(response));
        Assert.Equal("claimed", await Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task IRQ_09_claimant_gets_409_item_withdrawn_for_decide_and_release_after_a_producer_withdrawal()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        using var steward = factory.CreateReviewerClient("alpha-steward-1");
        await Post(steward, item, "claim");
        await db.AdminExecAsync($"update queue_item set state = 'withdrawn', claimed_by = null, claimed_at = null, withdrawn_reason = 'superseded' where item_id = '{item}'");

        var decide = await Post(steward, item, "decision", Decision("accept"));
        Assert.Equal(HttpStatusCode.Conflict, decide.StatusCode);
        Assert.Equal("item_withdrawn", await DecisionHttp.ReasonOf(decide));
        var release = await Post(steward, item, "release");
        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);
        Assert.Equal("item_withdrawn", await DecisionHttp.ReasonOf(release));
    }

    [Fact]
    public async Task IRQ_02_payload_on_an_identity_item_is_404_no_payload_for_a_steward_and_403_for_others()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        using var steward = factory.CreateReviewerClient("alpha-steward-1");
        using var sup = factory.CreateReviewerClient("alpha-sup-1");

        var ok = await steward.GetAsync($"{Items}/{item}/payload", Ct);
        Assert.Equal(HttpStatusCode.NotFound, ok.StatusCode);
        Assert.Equal("no_payload", await DecisionHttp.ReasonOf(ok));
        var refused = await sup.GetAsync($"{Items}/{item}/payload", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("role_not_permitted", await DecisionHttp.ReasonOf(refused));
    }

    [Fact]
    public async Task IRQ_06_deferred_rationale_is_visible_to_stewards_only_and_never_in_item_views_or_logs()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        using var steward = factory.CreateReviewerClient("alpha-steward-1");
        using var sup = factory.CreateReviewerClient("alpha-sup-1");
        await Post(steward, item, "claim");
        var defer = await Post(steward, item, "decision", Decision("defer"));
        Assert.DoesNotContain(Sentinel, await defer.Content.ReadAsStringAsync(Ct));

        var stewardEvents = await (await steward.GetAsync($"{Items}/{item}/events", Ct)).Content.ReadAsStringAsync(Ct);
        var supEvents = await (await sup.GetAsync($"{Items}/{item}/events", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains(Sentinel, stewardEvents);
        Assert.DoesNotContain(Sentinel, supEvents);
        using (var doc = JsonDocument.Parse(supEvents))
        {
            var deferred = doc.RootElement.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("event_type").GetString() == "deferred");
            Assert.Equal("defer", deferred.GetProperty("detail").GetProperty("decision").GetString());
            Assert.False(deferred.GetProperty("detail").TryGetProperty("rationale", out _));
        }

        Assert.DoesNotContain(Sentinel, await (await steward.GetAsync($"{Items}/{item}", Ct)).Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(Sentinel, await (await steward.GetAsync($"{Items}?kind=identity_review", Ct)).Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(Sentinel, factory.Logs.Text);
    }
}
