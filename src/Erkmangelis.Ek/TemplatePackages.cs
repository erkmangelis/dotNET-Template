using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Erkmangelis.Ek;

/// <summary>
/// Finds template packages by path or version. The tool carries the package of its own version;
/// other versions come from <c>--source</c> folders, the cache in ~/.ek/packages, or the
/// project's GitHub releases.
/// </summary>
public sealed partial class TemplatePackages(Catalog catalog, IReadOnlyList<string> sources, TextWriter log)
{
    public const string ReleaseUrl = "https://github.com/erkmangelis/dotNET-Template/releases/download/v{0}/{1}.{0}.nupkg";

    public static string ToolVersion { get; } = typeof(TemplatePackages).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];

    public static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ek", "packages");

    public string FileName(string version) => $"{catalog.Template.Package}.{version}.nupkg";

    /// <summary>The package's version, from a file name such as Erkmangelis.Templates.0.5.0.nupkg.</summary>
    public string VersionOf(string path)
    {
        var match = Regex.Match(Path.GetFileName(path), $"^{Regex.Escape(catalog.Template.Package)}\\.(\\d+\\.\\d+\\.\\d+.*)\\.nupkg$");
        return match.Success ? match.Groups[1].Value : throw new SpecException($"{path} is not a {catalog.Template.Package} package.");
    }

    public string? Bundled
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "template-package", FileName(ToolVersion));
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>A path to a .nupkg, or a version to look up.</summary>
    public async Task<string> FindAsync(string versionOrPath, CancellationToken cancellationToken)
    {
        if (versionOrPath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(versionOrPath) ? Path.GetFullPath(versionOrPath) : throw new SpecException($"Package {versionOrPath} does not exist.");
        }

        var version = versionOrPath;
        if (version == ToolVersion && Bundled is { } bundled)
        {
            return bundled;
        }

        foreach (var directory in sources.Append(CacheDirectory))
        {
            var candidate = Path.Combine(directory, FileName(version));
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, ReleaseUrl, version, catalog.Template.Package);
        log.WriteLine($"Downloading {url}");
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SpecException(
                $"Template package {version} is not in {string.Join(", ", sources.Append(CacheDirectory))} and the download answered {(int)response.StatusCode}. " +
                $"Pass the .nupkg with --from/--to, or a folder holding {FileName(version)} with --source.");
        }

        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, FileName(version));
        await using (var file = File.Create(path + ".partial"))
        {
            await response.Content.CopyToAsync(file, cancellationToken);
        }

        File.Move(path + ".partial", path, overwrite: true);
        return path;
    }
}

/// <summary>
/// Runs <c>dotnet new</c> with one template package installed in a private template hive, so the
/// user's installed templates are neither used nor changed.
/// </summary>
public sealed class DotnetNew : IDisposable
{
    private readonly string _hive;
    private readonly string _shortName;

    private DotnetNew(string hive, string shortName)
    {
        _hive = hive;
        _shortName = shortName;
    }

    public static async Task<DotnetNew> InstallAsync(string package, string shortName, CancellationToken cancellationToken)
    {
        var hive = Directory.CreateTempSubdirectory("ek-hive-").FullName;
        var runner = new DotnetNew(hive, shortName);
        await Run(["new", "install", package, "--debug:custom-hive", hive], cancellationToken);
        return runner;
    }

    public static IReadOnlyList<string> Arguments(string shortName, string name, string output, IEnumerable<string> templateArguments) =>
        ["new", shortName, "-n", name, "-o", output, .. templateArguments];

    public Task GenerateAsync(string name, string output, IEnumerable<string> templateArguments, CancellationToken cancellationToken) =>
        Run([.. Arguments(_shortName, name, output, templateArguments), "--debug:custom-hive", _hive], cancellationToken);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_hive, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary hive is harmless.
        }
    }

    private static async Task Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new SpecException($"dotnet {string.Join(' ', arguments)} failed:\n{await output}{await error}");
        }
    }
}
