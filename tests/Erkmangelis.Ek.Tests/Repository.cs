using System.Reflection;

namespace Erkmangelis.Ek.Tests;

internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string Template => Path.Combine(Root, "templates", "ek-api");

    public static string Catalog => Path.Combine(Root, "catalog");

    /// <summary>The built tool, next to the template package it carries.</summary>
    public static string Tool
    {
        get
        {
            var configuration = typeof(Repository).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            return Path.Combine(Root, "src", "Erkmangelis.Ek", "bin", configuration, "net10.0", "Erkmangelis.Ek.dll");
        }
    }

    public static string BundledPackage => Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Tool)!, "template-package"), "*.nupkg").Single();

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
