namespace Erkmangelis.Ek.Tests;

public sealed class SpecResolverTests
{
    private static readonly Catalog Catalog = Ek.Catalog.Embedded;

    private static ResolveResult Resolve(params (string Option, string Value)[] options) =>
        SpecResolver.Resolve(Catalog, "Acme.Notes", null, options.ToDictionary(pair => pair.Option, pair => pair.Value));

    [Fact]
    public void Nothing_given_resolves_to_the_defaults()
    {
        var spec = Resolve().Spec!;

        Assert.Equal(Catalog.Options.ToDictionary(option => option.Name, option => option.Default), spec.Options);
        Assert.Empty(spec.Implied);
    }

    [Fact]
    public void Sign_in_without_a_database_choice_gets_postgres_and_says_why()
    {
        var spec = Resolve(("auth", "identity")).Spec!;

        Assert.Equal("postgres", spec.Options["db"]);
        var implied = Assert.Single(spec.Implied);
        Assert.Equal("auth-needs-database", implied.Rule.Id);
    }

    [Fact]
    public void The_worker_brings_hangfire_and_hangfire_brings_a_database()
    {
        var spec = Resolve(("worker", "true")).Spec!;

        Assert.Equal("hangfire", spec.Options["jobs"]);
        Assert.Equal("postgres", spec.Options["db"]);
        Assert.Equal(["worker-needs-hangfire", "hangfire-needs-database"], spec.Implied.Select(item => item.Rule.Id));
    }

    [Fact]
    public void An_explicit_value_that_breaks_a_rule_is_rejected_with_the_reason()
    {
        var result = Resolve(("auth", "oidc"), ("db", "none"));

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains("auth oidc needs db postgres, sqlserver or sqlite, but db is none", error);
        Assert.Contains("Every sign-in mode stores users' data in a database.", error);
    }

    [Fact]
    public void The_worker_with_hosted_jobs_is_rejected()
    {
        var error = Assert.Single(Resolve(("worker", "true"), ("jobs", "hosted")).Errors);

        Assert.Contains("worker true needs jobs hangfire, but jobs is hosted", error);
    }

    [Fact]
    public void A_chained_conflict_names_the_rule_that_set_the_value()
    {
        var error = Assert.Single(Resolve(("worker", "true"), ("db", "none")).Errors);

        Assert.Contains("jobs hangfire (jobs hangfire was set because the worker runs", error);
        Assert.Contains("[hangfire-needs-database]", error);
    }

    [Fact]
    public void Options_given_next_to_a_preset_win()
    {
        var spec = SpecResolver.Resolve(Catalog, "Acme.Notes", "saas-api", new Dictionary<string, string> { ["db"] = "sqlite" }).Spec!;

        Assert.Equal("sqlite", spec.Options["db"]);
        Assert.Equal("identity", spec.Options["auth"]);
    }

    [Theory]
    [InlineData("color", "red", "Unknown option 'color'")]
    [InlineData("db", "mysql", "'mysql' is not a value of db")]
    [InlineData("worker", "yes", "'yes' is not a value of worker")]
    public void Unknown_options_and_values_are_rejected(string option, string value, string message) =>
        Assert.Contains(message, Assert.Single(Resolve((option, value)).Errors));

    [Theory]
    [InlineData("Acme.Notes", true)]
    [InlineData("RideSense", true)]
    [InlineData("acme-notes", false)]
    [InlineData("1Notes", false)]
    [InlineData("Acme..Notes", false)]
    public void Project_names_must_be_namespaces(string name, bool valid) =>
        Assert.Equal(valid, SpecResolver.Resolve(Catalog, name, null, new Dictionary<string, string>()).Succeeded);

    [Fact]
    public void An_excludes_rule_rejects_the_combination()
    {
        var modules = File.ReadAllText(Path.Combine(Repository.Catalog, "modules.json"))
            .Replace("\"rules\": [", """
                "rules": [
                  { "id": "no-aspire-on-sqlite", "when": { "db": ["sqlite"] }, "excludes": { "aspire": [true] }, "reason": "Just a test." },
                """);
        var catalog = Ek.Catalog.Load(modules, File.ReadAllText(Path.Combine(Repository.Catalog, "presets.json")));

        var result = SpecResolver.Resolve(catalog, "Acme.Notes", null, new Dictionary<string, string> { ["db"] = "sqlite", ["aspire"] = "true" });

        Assert.Contains("db sqlite cannot be combined with aspire true, but aspire is true. Just a test.", Assert.Single(result.Errors));
    }

    [Fact]
    public void A_spec_file_with_an_unknown_property_is_rejected()
    {
        var error = Assert.Throws<InvalidDataException>(() => ProjectSpec.Parse("""{ "specVersion": 1, "optoins": {} }"""));

        Assert.Contains("optoins", error.Message);
    }

    [Fact]
    public void A_written_spec_reads_back_to_the_same_options()
    {
        var spec = Resolve(("auth", "external"), ("worker", "true")).Spec!;

        var read = ProjectSpec.Parse(ProjectSpec.Write(Catalog, spec, "0.5.0"));

        Assert.Equal(spec.Options, read.Options);
        Assert.Equal("Acme.Notes", read.Name);
        Assert.Equal(spec.Options, SpecResolver.Resolve(Catalog, read).Spec!.Options);
    }

    [Fact]
    public void A_template_origin_from_before_specs_reads_as_a_spec()
    {
        var spec = ProjectSpec.Parse("""
            {
              "template": "Erkmangelis.Templates.Api",
              "package": "Erkmangelis.Templates",
              "version": "0.1.0",
              "options": { "auth": "identity", "db": "postgres", "agents": "claude" }
            }
            """);

        Assert.Null(spec.SpecVersion);
        Assert.Equal("0.1.0", spec.Version);
        Assert.Equal("postgres", SpecResolver.Resolve(Catalog, spec with { Name = "Acme.Notes" }).Spec!.Options["db"]);
    }
}
