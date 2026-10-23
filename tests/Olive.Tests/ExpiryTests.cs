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
