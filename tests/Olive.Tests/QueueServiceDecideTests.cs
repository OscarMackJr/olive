using Olive.Application;
using Olive.Catalog;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class QueueServiceDecideTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Role = "role:case_supervisor";

    private sealed class FixedResolver(UserIdentityLookup result) : IUserIdentityResolver
    {
        public Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeStore(QueueItemDetail? item) : IQueueStore
    {
        public int DecideCalls { get; private set; }
        public DecideResult Outcome { get; set; } = new DecideResult.NotFound();
        public bool? LastVerified { get; private set; }
        public int ClaimCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task<IReadOnlyList<QueueItemSummary>> ListAsync(Guid tenantId, string? kind, string? state, DateTimeOffset now, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueueItemDetail?> GetAsync(Guid tenantId, Guid itemId, CancellationToken ct) => Task.FromResult(item);
        public Task<IReadOnlyList<QueueItemEventRecord>?> EventsAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewerPayload?> GetPayloadAsync(Guid tenantId, Guid itemId, CancellationToken ct) =>
            Task.FromResult<ReviewerPayload?>(new ReviewerPayload(itemId, "action_approval", "crm.case_note@1", "{}", "j", "analyst.alpha@example.test", null, null));
        public Task<ClaimResult> ClaimAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct)
        {
            ClaimCalls++;
            return Task.FromResult<ClaimResult>(new ClaimResult.NotFound());
        }

        public Task<ReleaseResult> ReleaseAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct)
        {
            ReleaseCalls++;
            return Task.FromResult<ReleaseResult>(new ReleaseResult.NotFound());
        }

        public int CreateCalls { get; private set; }
        public Guid? LastTenant { get; private set; }
        public string? LastProducer { get; private set; }
        public Task<CreateIdentityResult> CreateIdentityItemAsync(Guid tenantId, string producer, string kind, string subtype, Guid producerRef, string subjectRef, string payloadRef, string rulesetVersion, string bodySha256, DateTimeOffset now, CancellationToken ct)
        {
            CreateCalls++;
            LastTenant = tenantId;
            LastProducer = producer;
            return Task.FromResult<CreateIdentityResult>(new CreateIdentityResult.Created(Guid.NewGuid(), "open"));
        }

        public Task<DecideResult> DecideAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, bool approveIdentityVerified, DateTimeOffset now, CancellationToken ct)
        {
            DecideCalls++;
            LastVerified = approveIdentityVerified;
            return Task.FromResult(Outcome);
        }
    }

    private static QueueItemDetail Item(string kind = "action_approval", string state = "claimed", string? claimedBy = "rev-1", DateTimeOffset? expires = null) =>
        new(Tenant, Guid.NewGuid(), kind, kind == "action_approval" ? "proposal" : "candidate_pair", "olive", Guid.NewGuid(), state, "s", "p", claimedBy, Now, expires,
            null, null, null, null, null, Guid.NewGuid());

    private static (QueueService Service, FakeStore Store, RecordingUserIdentityResolver Resolver) Build(QueueItemDetail? item, UserIdentityLookup? lookup = null)
    {
        var store = new FakeStore(item);
        var resolver = new RecordingUserIdentityResolver(new FixedResolver(lookup ?? new UserIdentityLookup.Resolved("other-oid", "tid-1")), [], []);
        var check = new ApprovalIdentityCheck(new UserIdentityVerifier(resolver, new UserIdentityVerifierOptions()));
        return (new QueueService(store, QueueKindRegistry.LoadEmbedded(), check, new ManualTimeProvider(Now)), store, resolver);
    }

    private static ReviewerContext Reviewer(string oid = "rev-1", params string[] groups) =>
        new(Tenant, oid, "tid-1", groups.Length == 0 ? [Role] : groups, "stub");

    [Fact]
    public async Task QUE_07_empty_reviewer_oid_throws_and_never_reaches_the_store()
    {
        var (service, store, _) = Build(Item());
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync(Reviewer(" "), Guid.NewGuid(), "approve", "ok", Ct));
        Assert.Equal(0, store.DecideCalls);
    }

    [Fact]
    public async Task QUE_07_identity_review_item_is_kind_not_supported_and_unknown_decision_is_invalid()
    {
        var (service, store, _) = Build(Item("identity_review"));
        Assert.IsType<DecisionResult.KindNotSupported>(await service.DecideAsync(Reviewer(), Guid.NewGuid(), "accept", "ok", Ct));

        var (service2, store2, _) = Build(Item());
        Assert.IsType<DecisionResult.InvalidRequest>(await service2.DecideAsync(Reviewer(), Guid.NewGuid(), "defer", "ok", Ct));
        Assert.IsType<DecisionResult.InvalidRequest>(await service2.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "　 ", Ct));
        Assert.IsType<DecisionResult.InvalidRequest>(await service2.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", new string('x', 4001), Ct));
        Assert.Equal(0, store.DecideCalls + store2.DecideCalls);
    }

    [Fact]
    public async Task D_09_reviewer_without_the_kind_role_is_refused_before_any_lookup()
    {
        var (service, store, resolver) = Build(Item());
        Assert.IsType<DecisionResult.RoleNotPermitted>(await service.DecideAsync(Reviewer("rev-1", "role:other"), Guid.NewGuid(), "approve", "ok", Ct));
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, store.DecideCalls);
    }

    [Fact]
    public async Task D_18_approve_calls_the_resolver_once_for_a_live_claim()
    {
        var (service, store, resolver) = Build(Item());
        store.Outcome = new DecideResult.Decided(Item(state: "decided"));
        Assert.IsType<DecisionResult.Decided>(await service.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "ok", Ct));
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task D_18_reject_never_calls_the_resolver_even_when_it_would_say_self_approval()
    {
        var (service, store, resolver) = Build(Item(), new UserIdentityLookup.Resolved("rev-1", "tid-1"));
        store.Outcome = new DecideResult.Decided(Item(state: "decided"));
        Assert.IsType<DecisionResult.Decided>(await service.DecideAsync(Reviewer(), Guid.NewGuid(), "reject", "ok", Ct));
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task D_18_expired_non_claimant_and_decided_items_never_call_the_resolver()
    {
        foreach (var item in new[] { Item(expires: Now), Item(claimedBy: "someone-else"), Item(state: "decided"), Item(state: "open", claimedBy: null) })
        {
            var (service, store, resolver) = Build(item);
            store.Outcome = new DecideResult.NotClaimant();
            await service.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "ok", Ct);
            Assert.Equal(0, resolver.CallCount);
        }
    }

    [Fact]
    public async Task APR_02_CR_01_approve_after_a_racing_claim_passes_identity_verified_false_to_the_store()
    {
        // Pre-read sees the item open (the claim has not landed yet), so the identity check is skipped and the flag must be false.
        var (service, store, resolver) = Build(Item(state: "open", claimedBy: null));
        store.Outcome = new DecideResult.NotClaimed();
        Assert.IsType<DecisionResult.NotClaimed>(await service.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "ok", Ct));
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(false, store.LastVerified);
    }

    [Fact]
    public async Task APR_02_CR_01_approve_after_a_passed_identity_check_passes_identity_verified_true_to_the_store()
    {
        var (service, store, _) = Build(Item());
        store.Outcome = new DecideResult.Decided(Item(state: "decided"));
        await service.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "ok", Ct);
        Assert.Equal(true, store.LastVerified);
    }

    [Fact]
    public async Task WR_01_rationale_with_a_nul_character_is_invalid_request_and_never_reaches_the_store()
    {
        var (service, store, _) = Build(Item());
        Assert.IsType<DecisionResult.InvalidRequest>(await service.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "a\0b", Ct));
        Assert.Equal(0, store.DecideCalls);
    }

    [Fact]
    public async Task WR_02_blank_reviewer_oid_is_refused_for_claim_and_release_before_the_store()
    {
        var (service, store, _) = Build(Item());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ClaimAsync(Reviewer(" "), Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReleaseAsync(Reviewer(""), Guid.NewGuid(), Ct));
        Assert.Equal(0, store.ClaimCalls + store.ReleaseCalls);
    }

    [Fact]
    public async Task APR_02_self_approval_unresolved_and_unavailable_refuse_without_deciding()
    {
        var (s1, st1, _) = Build(Item(), new UserIdentityLookup.Resolved("rev-1", "tid-1"));
        Assert.IsType<DecisionResult.SelfApproval>(await s1.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "ok", Ct));
        var (s2, st2, _) = Build(Item(), new UserIdentityLookup.Ambiguous());
        Assert.IsType<DecisionResult.Unresolved>(await s2.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "ok", Ct));
        var (s3, st3, _) = Build(Item(), new UserIdentityLookup.Unavailable());
        Assert.IsType<DecisionResult.Unavailable>(await s3.DecideAsync(Reviewer(), Guid.NewGuid(), "approve", "ok", Ct));
        Assert.Equal(0, st1.DecideCalls + st2.DecideCalls + st3.DecideCalls);
    }
}
