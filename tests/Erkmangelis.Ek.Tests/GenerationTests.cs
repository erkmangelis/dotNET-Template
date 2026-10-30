using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Erkmangelis.Ek.Tests;

/// <summary>
/// Runs the built tool and plain <c>dotnet new</c> against the template package the tool carries.
/// These prove the "no duplicated rules" promise from the outside: the CLI's rules give the same
/// options as the template's own computed symbols, and the CLI's output is plain dotnet new's.
/// </summary>
public sealed partial class GenerationTests(GenerationTests.Hive hive) : IClassFixture<GenerationTests.Hive>
{
    private static readonly Catalog Catalog = Ek.Catalog.Embedded;

    public static TheoryData<string> Presets => [.. Ek.Catalog.Embedded.Presets.Select(preset => preset.Name)];

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task The_cli_and_plain_dotnet_new_give_identical_output_for_a_preset(string preset)
    {
        using var work = new TemporaryDirectory();
        var cli = Path.Combine(work.Path, "cli");
        var plain = Path.Combine(work.Path, "plain");

        var output = await RunTool(work.Path, "new", "Acme.Notes", "--preset", preset, "-o", cli, "-y");
        var command = SameAsLine().Match(output).Groups[1].Value.Split(' ');
        Assert.Equal(["new", "ek-api", "-n", "Acme.Notes", "-o", cli], command[..6]);
        await hive.DotnetNew.GenerateAsync("Acme.Notes", plain, command[6..], CancellationToken.None);

        AssertSameTree(plain, cli);
    }

    [Fact]
    public async Task A_saved_spec_generates_the_same_project_again()
    {
        using var work = new TemporaryDirectory();
        var spec = Path.Combine(work.Path, "spec.json");
        await RunTool(work.Path, "new", "Acme.Notes", "--auth", "oidc", "--db", "sqlite", "--worker", "--save-spec", spec, "-o", Path.Combine(work.Path, "first"), "-y");

        await RunTool(work.Path, "new", "--spec", spec, "-o", Path.Combine(work.Path, "second"), "-y");

        AssertSameTree(Path.Combine(work.Path, "first"), Path.Combine(work.Path, "second"));
        Assert.Equal(
            ProjectSpec.Read(spec).Options,
            ProjectSpec.Read(Path.Combine(work.Path, "first", "template-origin.json")).Options);
    }

    [Fact]
    public async Task An_unsupported_combination_is_rejected_with_the_reason_and_nothing_is_generated()
    {
        using var work = new TemporaryDirectory();

        var (exitCode, output) = await RunToolRaw(work.Path, "new", "Acme.Notes", "--worker", "--jobs", "hosted", "-o", Path.Combine(work.Path, "out"), "-y");

        Assert.Equal(1, exitCode);
        Assert.Contains("The worker runs the API's Hangfire jobs in its own process.", output);
        Assert.False(Directory.Exists(Path.Combine(work.Path, "out")));
    }

    /// <summary>Each option value alone: what the CLI's rules decide must be what the template writes to template-origin.json.</summary>
    public static TheoryData<string, string> SingleValues
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var option in Ek.Catalog.Embedded.Options)
            {
                foreach (var choice in option.Choices)
                {
                    data.Add(option.Name, choice.Value);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SingleValues))]
    public async Task The_cli_rules_and_the_template_agree_on_the_effective_options(string option, string value)
    {
        using var work = new TemporaryDirectory();
        var expected = SpecResolver.Resolve(Catalog, "Acme.Notes", null, new Dictionary<string, string> { [option] = value }).Spec!.Options;

        await hive.DotnetNew.GenerateAsync("Acme.Notes", work.Path, [$"--{option}", value], CancellationToken.None);

        var origin = ProjectSpec.Read(Path.Combine(work.Path, "template-origin.json"));
        Assert.Equal(expected, origin.Options);
        Assert.Equal("Acme.Notes", origin.Name);
        Assert.Equal(TemplatePackages.ToolVersion, origin.Version);
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var expectedFiles = Files(expected);
        var actualFiles = Files(actual);
        Assert.Equal(expectedFiles.Keys, actualFiles.Keys);
        foreach (var (path, content) in expectedFiles)
        {
            Assert.True(content == actualFiles[path], $"{path} differs.");
        }
    }

    /// <summary>Every file, with GUIDs (fresh user secrets ids on every generation) scrubbed.</summary>
    private static SortedDictionary<string, string> Files(string root) => new(
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(root, file),
            file => Guid().Replace(File.ReadAllText(file), "guid")),
        StringComparer.Ordinal);

    private static async Task<string> RunTool(string directory, params string[] arguments)
    {
        var (exitCode, output) = await RunToolRaw(directory, arguments);
        Assert.True(exitCode == 0, output);
        return output;
    }

    private static async Task<(int ExitCode, string Output)> RunToolRaw(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(Repository.Tool);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    [GeneratedRegex("^Same as: dotnet (.+)$", RegexOptions.Multiline)]
    private static partial Regex SameAsLine();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    /// <summary>One private template hive with the bundled package, shared by the tests.</summary>
    public sealed class Hive : IAsyncLifetime
    {
        public DotnetNew DotnetNew { get; private set; } = null!;

        public async Task InitializeAsync() =>
            DotnetNew = await DotnetNew.InstallAsync(Repository.BundledPackage, "ek-api", CancellationToken.None);

        public Task DisposeAsync()
        {
            DotnetNew.Dispose();
            return Task.CompletedTask;
        }
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ek-test-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
