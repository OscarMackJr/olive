using System.Reflection;
using Olive.Contracts;
using Olive.Host.Problems;

namespace Olive.Tests;

[Trait("Category", "Unit")]
public sealed class ProblemReasonTitleTests
{
    private const string Fallback = "An internal error occurred.";

    public static TheoryData<string> Reasons()
    {
        var data = new TheoryData<string>();
        foreach (var f in typeof(ProblemReasons).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.IsLiteral && f.FieldType == typeof(string)) { data.Add((string)f.GetRawConstantValue()!); }
        }

        return data;
    }

    [Fact]
    public void Pitfall7_the_reflection_walk_finds_the_reason_constants()
    {
        var count = typeof(ProblemReasons).GetFields(BindingFlags.Public | BindingFlags.Static).Count(f => f.IsLiteral);
        Assert.True(count >= 20);
        Assert.Equal(count, Reasons().Count());
    }

    [Theory]
    [MemberData(nameof(Reasons))]
    public void Pitfall7_every_reason_has_a_static_title_except_internal_error_uses_the_fallback(string reason)
    {
        var title = ProblemResults.Create(400, reason).Title;
        if (reason == ProblemReasons.InternalError)
        {
            Assert.Equal(Fallback, title);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.NotEqual(Fallback, title);
        }
    }
}
