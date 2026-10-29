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
    /// <summary>
    /// The combinations that change code. <c>--agents</c> only adds or removes AGENTS.md and
    /// CLAUDE.md, so these use <c>claude</c> and the agents tests below cover the other two values.
    /// </summary>
    public static TheoryData<string, string, string> Combinations => new()
    {
        { "none", "none", "none" },
        { "none", "none", "smtp" },
        { "none", "postgres", "none" },
        { "none", "postgres", "smtp" },
        { "identity", "postgres", "none" },
        { "identity", "postgres", "smtp" },
        { "external", "postgres", "none" },
        { "external", "postgres", "smtp" },
        { "oidc", "postgres", "none" },
        { "oidc", "postgres", "smtp" },
        { "none", "sqlserver", "none" },
        { "identity", "sqlserver", "none" },
        { "oidc", "sqlserver", "none" },
        { "none", "sqlite", "none" },
        { "identity", "sqlite", "none" },
        { "oidc", "sqlite", "none" },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public Task Generated_files_match_the_snapshot(string auth, string db, string email) =>
        VerifyAsync(Scenario(auth, db, email, "claude"), auth, db, email, "claude");

    [Theory]
    [InlineData("none")]
    [InlineData("shared")]
    public Task Other_agent_choices_match_their_snapshots(string agents) =>
        VerifyAsync(Scenario("identity", "postgres", "smtp", agents), "identity", "postgres", "smtp", agents);

    [Theory]
    [InlineData("none", "AGENTS.md", "CLAUDE.md")]
    [InlineData("shared", "CLAUDE.md")]
    public void Agent_choices_only_add_or_remove_the_agent_files(string agents, params string[] removed)
    {
        var claude = SnapshotFiles(Scenario("identity", "postgres", "smtp", "claude"));
        var other = SnapshotFiles(Scenario("identity", "postgres", "smtp", agents));

        // Snapshot paths start with the output folder. template-origin.json records the choice itself.
        string[] skipped = [.. removed.Select(file => $"ek-api/{file}"), "ek-api/template-origin.json"];
        AssertSameFiles(
            claude.Where(file => !skipped.Contains(file.Key)).ToDictionary(),
            other.Where(file => file.Key != "ek-api/template-origin.json").ToDictionary(),
            $"--agents {agents}");
    }

    [Fact]
    public Task Accounts_without_a_database_match_their_snapshot() =>
        VerifyAsync(Scenario("identity", "none", "none", "claude"), "identity", "none", "none", "claude");

    [Fact]
    public void Accounts_without_a_database_generate_the_postgres_output()
    {
        // Every --auth mode but none turns PostgreSQL on, so --db none must give the same files as --db postgres.
        AssertSameFiles(
            SnapshotFiles(Scenario("identity", "postgres", "none", "claude")),
            SnapshotFiles(Scenario("identity", "none", "none", "claude")),
            "--auth identity --db none");
    }

    private static string Scenario(string auth, string db, string email, string agents) =>
        $"auth-{auth}.db-{db}.email-{email}.agents-{agents}";

    private static void AssertSameFiles(Dictionary<string, string> expected, Dictionary<string, string> actual, string what)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, content) in expected)
        {
            Assert.True(content == actual[path], $"{path} differs with {what}.");
        }
    }

    private static Dictionary<string, string> SnapshotFiles(string scenario)
    {
        var root = Path.Combine(RepositoryPaths.Snapshots, $"_.{scenario}.verified");
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllText, StringComparer.Ordinal);
    }

    private static async Task VerifyAsync(string scenario, string auth, string db, string email, string agents)
    {
        string[] templateArgs = ["--auth", auth, "--db", db, "--email", email, "--agents", agents];
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
