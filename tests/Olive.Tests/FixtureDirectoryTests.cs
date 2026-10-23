using Olive.Application;
using Olive.Integrations.Entra;

namespace Olive.Tests;

public sealed class FixtureDirectoryTests
{
    private static readonly Guid Alpha = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");
    private static readonly Guid Beta = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");
    private const string Ent = "ent:crm.case_note.write";

    [Fact]
    public async Task POL_02_default_fixtures_resolve_known_users()
    {
        var r = FixtureDirectoryGroupResolver.CreateDefault();
        var a = Assert.IsType<DirectoryLookup.Found>(await r.ResolveAsync(Alpha, "analyst.alpha@example.test", TestContext.Current.CancellationToken));
        Assert.Contains(Ent, a.Groups);
        var b = Assert.IsType<DirectoryLookup.Found>(await r.ResolveAsync(Beta, "analyst.beta@example.test", TestContext.Current.CancellationToken));
        Assert.Contains(Ent, b.Groups);
    }

    [Fact]
    public async Task POL_02_unlisted_user_and_cross_tenant_are_unknown()
    {
        var r = FixtureDirectoryGroupResolver.CreateDefault();
        Assert.IsType<DirectoryLookup.UnknownUser>(await r.ResolveAsync(Alpha, "nobody@example.test", TestContext.Current.CancellationToken));
        Assert.IsType<DirectoryLookup.UnknownUser>(await r.ResolveAsync(Beta, "analyst.alpha@example.test", TestContext.Current.CancellationToken));
        Assert.IsType<DirectoryLookup.UnknownUser>(await r.ResolveAsync(Alpha, "Analyst.Alpha@example.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task POL_02_registered_user_with_no_groups_is_found_empty()
    {
        var r = FixtureDirectoryGroupResolver.CreateDefault();
        r.Register(Alpha, "empty@example.test", []);
        var f = Assert.IsType<DirectoryLookup.Found>(await r.ResolveAsync(Alpha, "empty@example.test", TestContext.Current.CancellationToken));
        Assert.Empty(f.Groups);
    }

    [Fact]
    public void POL_02_stub_is_non_production_only()
    {
        Assert.IsAssignableFrom<INonProductionOnly>(FixtureDirectoryGroupResolver.CreateDefault());
    }
}
