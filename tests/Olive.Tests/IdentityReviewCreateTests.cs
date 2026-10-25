using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Olive.Application;
using Olive.Catalog;
using Olive.Persistence;

namespace Olive.Tests;

// IRQ-01 / IRQ-02 / IRQ-03 / TC-IRQ-11: the producer create path, from service validation down to HTTP.
public sealed class IdentityReviewCreateTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private const string AlphaToken = "fixture-service-token-bluto-alpha";
    private const string BetaToken = "fixture-service-token-bluto-beta";
    private const string Ref = "3f2b8c1e-9d4a-4b7e-8c21-5a6d7e8f9012";
    private static readonly Guid Alpha = TestPaths.TenantAlpha;
    private static readonly Guid Beta = TestPaths.TenantBeta;
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hash(char c = 'a') => new(c, 64);

    private static string Body(string producerRef = Ref, string ruleset = "rules-1", string subtype = "candidate_pair", string extra = "") =>
        "{" + extra + "\"kind\":\"identity_review\",\"subtype\":\"" + subtype + "\",\"producer_ref\":\"" + producerRef +
        "\",\"subject_ref\":\"bluto:candidate/" + producerRef + "\",\"payload_ref\":\"bluto:review-evidence/" + producerRef +
        "\",\"ruleset_version\":\"" + ruleset + "\"}";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string ReasonOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString() ?? "";

    private static PostgresQueueStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)));

    // ---- service level (no store call on invalid input) ----

    private sealed class CountingStore : IQueueStore
    {
        public int Calls { get; private set; }
        public Guid? Tenant { get; private set; }
        public string? Producer { get; private set; }

        public Task<CreateIdentityResult> CreateIdentityItemAsync(Guid tenantId, string producer, string kind, string subtype, Guid producerRef, string subjectRef, string payloadRef, string rulesetVersion, string bodySha256, DateTimeOffset now, CancellationToken ct)
        {
            Calls++;
            Tenant = tenantId;
            Producer = producer;
            return Task.FromResult<CreateIdentityResult>(new CreateIdentityResult.Created(Guid.NewGuid(), "open"));
        }

        public Task<IReadOnlyList<QueueItemSummary>> ListAsync(Guid tenantId, string? kind, string? state, DateTimeOffset now, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueueItemDetail?> GetAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<QueueItemEventRecord>?> EventsAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewerPayload?> GetPayloadAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClaimResult> ClaimAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReleaseResult> ReleaseAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<DecideResult> DecideAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, bool approveIdentityVerified, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (QueueService Service, CountingStore Store) BuildService()
    {
        var store = new CountingStore();
        var resolver = new RecordingUserIdentityResolver(new FixedResolverStub(), [], []);
        var check = new ApprovalIdentityCheck(new UserIdentityVerifier(resolver, new UserIdentityVerifierOptions()));
        return (new QueueService(store, QueueKindRegistry.LoadEmbedded(), check, new ManualTimeProvider(Now)), store);
    }

    private sealed class FixedResolverStub : IUserIdentityResolver
    {
        public Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string identifier, CancellationToken ct) =>
            Task.FromResult<UserIdentityLookup>(new UserIdentityLookup.Resolved("oid", "tid"));
    }

    private static ProducerContext Producer(Guid tenant) => new(tenant, "bluto", "bluto-stub",
        new Dictionary<string, IReadOnlySet<string>> { ["identity_review"] = new HashSet<string> { "candidate_pair" } });

    private static CreateItemRequest Request(string producerRef = Ref, string? subject = null, string? payload = null, string ruleset = "rules-1", string kind = "identity_review", string subtype = "candidate_pair") =>
        new(kind, subtype, producerRef, subject ?? "bluto:candidate/" + producerRef, payload ?? "bluto:review-evidence/" + producerRef, ruleset);

    public static TheoryData<string, string> BadRequests => new()
    {
        { "subject", "bluto:candidate/00000000-0000-4000-8000-000000000001" },
        { "payload", "bluto:review-evidence/00000000-0000-4000-8000-000000000001" },
        { "upper", Ref.ToUpperInvariant() },
        { "ruleset-space", "has space" },
        { "ruleset-record", "Jane Doe, 1980-01-01" },
        { "ruleset-long", new string('a', 129) },
        { "ruleset-empty", "" },
        { "kind", "action_approval" },
        { "subtype", "link_challenge" },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    [Trait("Category", "Unit")]
    public async Task IRQ_01_service_rejects_bad_values_without_touching_the_store(string which, string value)
    {
        var (service, store) = BuildService();
        var request = which switch
        {
            "subject" => Request(subject: value),
            "payload" => Request(payload: value),
            "upper" => Request(Ref.ToUpperInvariant()),
            "kind" => Request(kind: value),
            "subtype" => Request(subtype: value),
            _ => Request(ruleset: value),
        };
        Assert.IsType<CreateResult.InvalidRequest>(await service.CreateAsync(Producer(Alpha), request, Hash(), Ct));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IRQ_01_service_rejects_a_malformed_body_hash_without_touching_the_store()
    {
        var (service, store) = BuildService();
        Assert.IsType<CreateResult.InvalidRequest>(await service.CreateAsync(Producer(Alpha), Request(), "ABC", Ct));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IRQ_01_service_takes_producer_and_tenant_from_the_context()
    {
        var (service, store) = BuildService();
        Assert.IsType<CreateResult.Created>(await service.CreateAsync(Producer(Beta), Request(), Hash(), Ct));
        Assert.Equal(1, store.Calls);
        Assert.Equal(Beta, store.Tenant);
        Assert.Equal("bluto", store.Producer);
    }

    // ---- store level ----

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IRQ_02_store_first_create_writes_one_row_and_one_created_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var r = await store.CreateIdentityItemAsync(Alpha, "bluto", "identity_review", "candidate_pair", Guid.Parse(Ref), "bluto:candidate/" + Ref, "bluto:review-evidence/" + Ref, "rules-1", Hash(), Now, Ct);
        var created = Assert.IsType<CreateIdentityResult.Created>(r);
        Assert.Equal("open", created.State);

        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item"));
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item where kind='identity_review' and subtype='candidate_pair' and producer='bluto' and ruleset_version='rules-1' and body_sha256='" + Hash() + "'"));
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item_event where event_type='created' and from_state is null and to_state='open' and actor='bluto' and detail is null"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IRQ_02_store_replay_returns_the_current_state_and_writes_nothing_more()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var first = Assert.IsType<CreateIdentityResult.Created>(await store.CreateIdentityItemAsync(Alpha, "bluto", "identity_review", "candidate_pair", Guid.Parse(Ref), "bluto:candidate/" + Ref, "bluto:review-evidence/" + Ref, "rules-1", Hash(), Now, Ct));
        await db.AdminExecAsync("update queue_item set state='claimed', claimed_by='rev-1', claimed_at=now() where item_id='" + first.ItemId + "'");

        var again = Assert.IsType<CreateIdentityResult.Replay>(await store.CreateIdentityItemAsync(Alpha, "bluto", "identity_review", "candidate_pair", Guid.Parse(Ref), "bluto:candidate/" + Ref, "bluto:review-evidence/" + Ref, "rules-1", Hash(), Now, Ct));
        Assert.Equal(first.ItemId, again.ItemId);
        Assert.Equal("claimed", again.State);
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item"));
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item_event"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IRQ_02_store_different_hash_conflicts_and_writes_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        await store.CreateIdentityItemAsync(Alpha, "bluto", "identity_review", "candidate_pair", Guid.Parse(Ref), "bluto:candidate/" + Ref, "bluto:review-evidence/" + Ref, "rules-1", Hash(), Now, Ct);
        var r = await store.CreateIdentityItemAsync(Alpha, "bluto", "identity_review", "candidate_pair", Guid.Parse(Ref), "bluto:candidate/" + Ref, "bluto:review-evidence/" + Ref, "rules-2", Hash('b'), Now, Ct);
        Assert.IsType<CreateIdentityResult.Conflict>(r);
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item"));
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item_event"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IRQ_02_store_same_producer_ref_in_another_tenant_is_a_separate_item()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var a = Assert.IsType<CreateIdentityResult.Created>(await store.CreateIdentityItemAsync(Alpha, "bluto", "identity_review", "candidate_pair", Guid.Parse(Ref), "bluto:candidate/" + Ref, "bluto:review-evidence/" + Ref, "rules-1", Hash(), Now, Ct));
        var b = Assert.IsType<CreateIdentityResult.Created>(await store.CreateIdentityItemAsync(Beta, "bluto", "identity_review", "candidate_pair", Guid.Parse(Ref), "bluto:candidate/" + Ref, "bluto:review-evidence/" + Ref, "rules-1", Hash(), Now, Ct));
        Assert.NotEqual(a.ItemId, b.ItemId);
        Assert.Equal(2, await db.AdminCountAsync("select count(*) from queue_item"));
    }

    // ---- HTTP ----

    private async Task<(OliveApiFactory Factory, TestDatabase Db)> StartAsync()
    {
        var db = await fixture.CreateDatabaseAsync();
        return (new OliveApiFactory(db), db);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IRQ_01_created_item_takes_producer_and_tenant_from_the_token_and_the_response_is_slim()
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(BetaToken);
        var response = await client.PostAsync(Items, Json(Body()), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["item_id", "state"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("open", root.GetProperty("state").GetString());
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item where producer='bluto' and tenant_id='" + Beta + "' and kind='identity_review'"));
        Assert.Equal(0, await db.AdminCountAsync("select count(*) from queue_item where tenant_id='" + Alpha + "'"));
    }

    [Theory]
    [InlineData("\"payload\":{\"name\":\"Jane\"},")]
    [InlineData("\"name\":\"Jane Doe\",")]
    [InlineData("\"extra\":1,")]
    public async Task IRQ_01_inline_or_unknown_fields_are_rejected_and_nothing_is_written(string extra)
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.PostAsync(Items, Json(Body(extra: extra)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(0, await db.AdminCountAsync("select count(*) from queue_item"));
    }

    [Theory]
    [InlineData("rules 1")]
    [InlineData("Jane Doe 1980-01-01")]
    public async Task IRQ_01_record_like_ruleset_version_is_rejected(string ruleset)
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.PostAsync(Items, Json(Body(ruleset: ruleset)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await db.AdminCountAsync("select count(*) from queue_item"));
    }

    [Fact]
    public async Task IRQ_02_identical_body_replays_with_reordered_keys_and_a_changed_body_conflicts()
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);

        var first = await client.PostAsync(Items, Json(Body()), Ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("item_id").GetGuid();

        var reordered = "{\"ruleset_version\":\"rules-1\",\"payload_ref\":\"bluto:review-evidence/" + Ref + "\",\"subject_ref\":\"bluto:candidate/" + Ref +
            "\",\"producer_ref\":\"" + Ref + "\",\"subtype\":\"candidate_pair\",\"kind\":\"identity_review\"}";
        var replay = await client.PostAsync(Items, Json(reordered), Ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstId, JsonDocument.Parse(await replay.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("item_id").GetGuid());

        var conflict = await client.PostAsync(Items, Json(Body(ruleset: "rules-2")), Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", ReasonOf(await conflict.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item"));
    }

    [Theory]
    [InlineData("tenant_id", "\"5325ffe9-5727-4d88-9832-aebabb8d0538\"")]
    [InlineData("tenant_id", "\"f92c9dba-50f1-4cad-9690-5d79f8ded335\"")]
    [InlineData("producer", "\"other-producer\"")]
    [InlineData("item_id", "\"0b1f2a3c-4d5e-4f60-8a7b-9c0d1e2f3a4b\"")]
    [InlineData("state", "\"decided\"")]
    [InlineData("decision", "\"accept\"")]
    [InlineData("decided_by", "\"someone\"")]
    [InlineData("rationale", "\"because\"")]
    public async Task IRQ_03_server_assigned_fields_are_rejected_without_echo(string name, string value)
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.PostAsync(Items, Json(Body(extra: "\"" + name + "\":" + value + ",")), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("server_assigned_field", ReasonOf(text));
        Assert.DoesNotContain(name, text.Replace("server_assigned_field", ""));
        Assert.DoesNotContain(value.Trim('"'), text);
        Assert.Equal(0, await db.AdminCountAsync("select count(*) from queue_item"));

        var ok = await client.PostAsync(Items, Json(Body()), Ct);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(1, await db.AdminCountAsync("select count(*) from queue_item where tenant_id='" + Alpha + "'"));
    }

    [Fact]
    public async Task IRQ_03_server_assigned_field_wins_over_an_unknown_field()
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.PostAsync(Items, Json(Body(extra: "\"zzz\":1,\"tenant_id\":\"x\",")), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("server_assigned_field", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task IRQ_11_link_challenge_subtype_is_rejected()
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.PostAsync(Items, Json(Body(subtype: "link_challenge")), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(0, await db.AdminCountAsync("select count(*) from queue_item"));
    }

    [Theory]
    [InlineData("{\"kind\":\"identity_review\",\"kind\":\"identity_review\"}", "application/json")]
    [InlineData("[1]", "application/json")]
    [InlineData("\"x\"", "application/json")]
    [InlineData("{\"ruleset_version\":\"a\\ud800\"}", "application/json")]
    [InlineData("{}", "text/plain")]
    public async Task IRQ_01_malformed_bodies_are_invalid_request(string body, string contentType)
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.PostAsync(Items, new StringContent(body, Encoding.UTF8, contentType), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task IRQ_01_oversize_body_is_413()
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(AlphaToken);
        var response = await client.PostAsync(Items, Json(Body(extra: "\"x\":\"" + new string('a', 1_100_000) + "\",")), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("fixture-key-liam-alpha")]
    [InlineData("fixture-key-user")]
    [InlineData("fixture-service-token-delegated-agent")]
    public async Task IRQ_04_non_producer_credentials_cannot_create(string token)
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateServiceClient(token);
        var response = await client.PostAsync(Items, Json(Body()), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(0, await db.AdminCountAsync("select count(*) from queue_item"));
    }

    [Fact]
    public async Task IRQ_04_reviewer_header_cannot_create()
    {
        var (factory, db) = await StartAsync();
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateReviewerClient("alpha-sup-1");
        var response = await client.PostAsync(Items, Json(Body()), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("principal_not_permitted", ReasonOf(await response.Content.ReadAsStringAsync(Ct)));
    }
}
