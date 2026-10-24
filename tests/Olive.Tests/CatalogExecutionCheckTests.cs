using System.Text.Json;
using Olive.Catalog;

namespace Olive.Tests;

public sealed class CatalogExecutionCheckTests
{
    private const string Good = "{\"case_ref\":\"c\",\"note_ref\":\"n\"}";
    private static string RealVocabulary => Path.Combine(TestPaths.Root, "contracts", "vendored", "vocabulary.yaml");

    private static CatalogSet Custom(string schema, string entryJson = "{\"target_system\":\"nexus_crm\"}") =>
        new([new CatalogRelease(1, [new CatalogEntry("crm.case_note@1", JsonDocument.Parse(entryJson).RootElement, SchemaValidator.Compile(schema))])]);

    [Fact]
    public void EXE_03_current_entry_with_valid_parameters_is_executable_with_target_system()
    {
        var check = new CatalogExecutionCheck(CatalogLoader.LoadEmbedded(RealVocabulary));

        var r = check.Check("crm.case_note@1", Good);

        Assert.True(r.Executable);
        Assert.Equal("nexus_crm", r.TargetSystem);
    }

    [Fact]
    public void EXE_03_entry_missing_from_current_release_is_not_executable()
    {
        var check = new CatalogExecutionCheck(Custom("{\"type\":\"object\"}"));

        Assert.False(check.Check("crm.other@1", Good).Executable);
    }

    [Fact]
    public void EXE_03_parameters_that_no_longer_validate_are_not_executable()
    {
        var check = new CatalogExecutionCheck(Custom("{\"type\":\"object\",\"required\":[\"extra\"]}"));

        Assert.False(check.Check("crm.case_note@1", Good).Executable);
    }

    [Fact]
    public void EXE_03_unparseable_parameters_are_not_executable_and_throw_nothing()
    {
        var check = new CatalogExecutionCheck(Custom("{\"type\":\"object\"}"));

        Assert.False(check.Check("crm.case_note@1", "{not json").Executable);
    }

    [Fact]
    public void EXE_03_missing_target_system_yields_null_target()
    {
        var check = new CatalogExecutionCheck(Custom("{\"type\":\"object\"}", "{}"));

        var r = check.Check("crm.case_note@1", Good);

        Assert.True(r.Executable);
        Assert.Null(r.TargetSystem);
    }
}
