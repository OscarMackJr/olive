using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ExpiryTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset NewStart()
    {
        var n = DateTimeOffset.UtcNow;
        return new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, TimeSpan.Zero);
    }

    private static OliveApiFactory Factory(TestDatabase db, ManualTimeProvider clock, TimeSpan? keyLife = null)
    {
        IKeyIntrospector? introspector = null;
        if (keyLife is { } life)
        {
            var json = File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "introspection", "liam-alpha.json"))
                .Replace("2099-01-01T00:00:00Z", (clock.GetUtcNow() + life).ToString("yyyy-MM-ddTHH:mm:ssZ"));
            introspector = new StubKeyIntrospector(new Dictionary<string, string> { [ApiProposalTests.AlphaKey] = json }, clock);
        }

        return new OliveApiFactory(db, introspector, configureServices: s => s.AddSingleton<TimeProvider>(clock));
    }

    // Advances the clock while the request is in flight (after authentication), as slow trace or directory calls would.
    private sealed class AdvancingDirectory(IDirectoryGroupResolver inner, ManualTimeProvider clock, TimeSpan by) : IDirectoryGroupResolver
    {
        public Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct)
        {
            clock.Advance(by);
            return inner.ResolveAsync(tenantId, onBehalfOf, ct);
        }
    }

    private static OliveApiFactory AdvancingFactory(TestDatabase db, ManualTimeProvider clock, TimeSpan keyLife, TimeSpan advanceBy)
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "introspection", "liam-alpha.json"))
            .Replace("2099-01-01T00:00:00Z", (clock.GetUtcNow() + keyLife).ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var introspector = new StubKeyIntrospector(new Dictionary<string, string> { [ApiProposalTests.AlphaKey] = json }, clock);
        var directory = new AdvancingDirectory(Olive.Integrations.Entra.FixtureDirectoryGroupResolver.CreateDefault(), clock, advanceBy);
        return new OliveApiFactory(db, introspector, configureServices: s => s.AddSingleton<TimeProvider>(clock), innerDirectory: directory);
    }

    private static string Body(string run, string? extra = null) =>
        ApiProposalTests.BodyFor(run).TrimEnd('}') + (extra is null ? "" : "," + extra) + "}";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<DateTimeOffset?> SubmitAndGetExpiryAsync(HttpClient c, string body)
    {
        var post = await c.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var id = (await JsonOf(post)).GetProperty("proposal_id").GetGuid();
        var got = await JsonOf(await c.GetAsync($"/v1/proposals/{id}", Ct));
        return got.TryGetProperty("expires_at", out var e) ? e.GetDateTimeOffset() : null;
    }

    [Fact]
    public async Task POL_07_default_is_8h()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(clock.GetUtcNow().AddSeconds(28800), await SubmitAndGetExpiryAsync(alpha, Body("d1")));
    }

    [Fact]
    public async Task POL_07_shortens()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(clock.GetUtcNow().AddSeconds(600), await SubmitAndGetExpiryAsync(alpha, Body("s1", "\"expires_in_seconds\":600")));
    }

    [Fact]
    public async Task POL_07_exactly_max_is_accepted()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(clock.GetUtcNow().AddSeconds(28800), await SubmitAndGetExpiryAsync(alpha, Body("m1", "\"expires_in_seconds\":28800")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("28801")]
    [InlineData("1.5")]
    [InlineData("\"10\"")]
    public async Task POL_07_zero_negative_or_over_max_is_400_and_nothing_stored(string value)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Body("bad", "\"expires_in_seconds\":" + value)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("99999999999999999999")]
    public async Task POL_07_float_form_1_0_is_400_not_500(string value)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Body("flt", "\"expires_in_seconds\":" + value)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    [Fact]
    public async Task POL_07_key_expiry_caps_silently()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock, TimeSpan.FromSeconds(100));
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(clock.GetUtcNow().AddSeconds(100), await SubmitAndGetExpiryAsync(alpha, Body("k1", "\"expires_in_seconds\":600")));
    }

    // WR-04: a key that expires while the request is in flight never yields an accepted proposal with expires_at <= now.
    [Fact]
    public async Task POL_07_key_expired_during_request_is_401_key_inactive_and_nothing_stored()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = AdvancingFactory(db, clock, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100));
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var r = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Body("late")), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("key_inactive", (await JsonOf(r)).GetProperty("reason").GetString());
        Assert.Equal(0, await ApiProposalTests.CountRowsAsync(db, ApiProposalTests.Alpha));
    }

    // Positive control: the same seam with a key still live by one second is accepted and capped at the key expiry.
    [Fact]
    public async Task POL_07_key_still_live_after_in_flight_delay_is_accepted_and_capped()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var start = NewStart();
        var clock = new ManualTimeProvider(start);
        await using var factory = AdvancingFactory(db, clock, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(99));
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        Assert.Equal(start.AddSeconds(100), await SubmitAndGetExpiryAsync(alpha, Body("ontime")));
    }

    [Fact]
    public async Task POL_07_expires_at_is_absolute_and_get_exposes_it()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var post = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Body("a1")), Ct);
        var id = (await JsonOf(post)).GetProperty("proposal_id").GetGuid();
        var first = (await JsonOf(await alpha.GetAsync($"/v1/proposals/{id}", Ct))).GetProperty("expires_at").GetDateTimeOffset();
        clock.Advance(TimeSpan.FromHours(3));
        var second = (await JsonOf(await alpha.GetAsync($"/v1/proposals/{id}", Ct))).GetProperty("expires_at").GetDateTimeOffset();
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task POL_07_refused_proposals_have_no_expires_at()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = Body("r1").Replace("crm.case_note@1", "crm.unknown@1");
        Assert.Null(await SubmitAndGetExpiryAsync(alpha, body));
    }

    [Fact]
    public async Task POL_07_changed_expires_in_seconds_under_same_run_id_is_409_while_identical_is_200()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(NewStart());
        await using var factory = Factory(db, clock);
        using var alpha = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = Body("h1", "\"expires_in_seconds\":600");
        Assert.Equal(HttpStatusCode.Created, (await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct)).StatusCode);
        var changed = await alpha.PostAsync("/v1/proposals", ApiProposalTests.Json(Body("h1", "\"expires_in_seconds\":601")), Ct);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }
}
