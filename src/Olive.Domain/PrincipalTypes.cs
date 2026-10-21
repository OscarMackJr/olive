namespace Olive.Domain;

public static class PrincipalTypes
{
    public const string User = "user";
    public const string Application = "application";
    public const string Agent = "agent";

    public static readonly IReadOnlyList<string> All = [User, Application, Agent];
}
