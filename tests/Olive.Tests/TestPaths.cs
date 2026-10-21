namespace Olive.Tests;

internal static class TestPaths
{
    public static readonly Guid TenantAlpha = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");
    public static readonly Guid TenantBeta = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");

    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Olive.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Olive.sln not found above " + AppContext.BaseDirectory);
    }
}
