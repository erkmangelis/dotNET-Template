using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.TemplateEngine.Authoring.TemplateVerifier;

namespace Erkmangelis.Templates.Tests;

/// <summary>
/// Pins every generated file for each option combination, so any change to the output shows up
/// as a snapshot diff in review. After an intended change, run scripts/accept-snapshots.sh.
/// </summary>
public sealed partial class TemplateSnapshotTests
{
    public static TheoryData<string, string, string> Combinations => new()
    {
        { "none", "none", "none" },
        { "none", "none", "shared" },
        { "none", "none", "claude" },
        { "none", "postgres", "none" },
        { "none", "postgres", "shared" },
        { "none", "postgres", "claude" },
        { "identity", "postgres", "none" },
        { "identity", "postgres", "shared" },
        { "identity", "postgres", "claude" },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public Task Generated_files_match_the_snapshot(string auth, string db, string agents) =>
        VerifyAsync(scenario: $"auth-{auth}.db-{db}.agents-{agents}", "--auth", auth, "--db", db, "--agents", agents);

    [Fact]
    public Task Identity_without_a_database_matches_its_snapshot() =>
        VerifyAsync(scenario: "auth-identity.db-none.agents-shared", "--auth", "identity", "--db", "none", "--agents", "shared");

    [Fact]
    public void Identity_without_a_database_generates_the_postgres_output()
    {
        // --auth identity always turns PostgreSQL on, so --db none must give the same files as --db postgres.
        var withoutDb = SnapshotFiles("auth-identity.db-none.agents-shared");
        var withDb = SnapshotFiles("auth-identity.db-postgres.agents-shared");

        Assert.Equal(withDb.Keys.Order(StringComparer.Ordinal), withoutDb.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, content) in withDb)
        {
            Assert.True(content == withoutDb[path], $"{path} differs between --db none and --db postgres with --auth identity.");
        }
    }

    private static Dictionary<string, string> SnapshotFiles(string scenario)
    {
        var root = Path.Combine(RepositoryPaths.Snapshots, $"_.{scenario}.verified");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllText, StringComparer.Ordinal);
    }

    private static async Task VerifyAsync(string scenario, params string[] templateArgs)
    {
        var options = new TemplateVerifierOptions(templateName: "ek-api")
        {
            TemplatePath = RepositoryPaths.Template,
            TemplateSpecificArgs = ["-n", "Acme.Notes", .. templateArgs],
            SnapshotsDirectory = RepositoryPaths.Snapshots,
            ScenarioName = scenario,
            DoNotAppendTemplateArgsToScenarioName = true,
            DoNotPrependCallerMethodNameToScenarioName = true,
            DoNotPrependTemplateNameToScenarioName = true,
            DisableDiffTool = true,
            VerifyCommandOutput = false,
            // Package versions change with every Dependabot update; the acceptance test covers them.
            VerificationExcludePatterns = ["**/Directory.Packages.props", "**/.config/dotnet-tools.json"],
        }.WithCustomScrubbers(ScrubbersDefinition.Empty.AddScrubber(ScrubGuids));

        await new VerificationEngine(NullLogger.Instance).Execute(Options.Create(options));
    }

    private static void ScrubGuids(System.Text.StringBuilder content)
    {
        var scrubbed = GuidPattern().Replace(content.ToString(), "00000000-0000-0000-0000-000000000000");
        content.Clear().Append(scrubbed);
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}
