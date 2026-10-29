namespace Erkmangelis.Templates.Tests;

internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string Template => Path.Combine(Root, "templates", "ek-api");

    public static string Snapshots => Path.Combine(Root, "tests", "Erkmangelis.Templates.Tests", "Snapshots");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Erkmangelis.Templates.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Run the tests from inside the template repository.");
    }
}
