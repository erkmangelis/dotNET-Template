using System.Text.Json;

namespace Erkmangelis.Ek.Tests;

/// <summary>The catalog must describe the template's real symbols, and nothing else.</summary>
public sealed class CatalogTests
{
    private static readonly Catalog Catalog = Ek.Catalog.Embedded;

    [Fact]
    public void Every_template_parameter_is_a_catalog_option_with_the_same_values_and_default()
    {
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Template, ".template.config", "template.json")));
        var parameters = template.RootElement.GetProperty("symbols").EnumerateObject()
            .Where(symbol => symbol.Value.GetProperty("type").GetString() == "parameter")
            .ToList();

        Assert.Equal(parameters.Select(symbol => symbol.Name), Catalog.Options.Select(option => option.Name));
        foreach (var symbol in parameters)
        {
            var option = Catalog.FindOption(symbol.Name)!;
            Assert.Equal(symbol.Value.GetProperty("datatype").GetString(), option.Type);
            Assert.Equal(symbol.Value.GetProperty("defaultValue").GetString(), option.Default);
            Assert.Equal(symbol.Value.GetProperty("description").GetString(), option.Description);
            if (!option.IsBool)
            {
                var choices = symbol.Value.GetProperty("choices").EnumerateArray()
                    .Select(choice => new Choice(choice.GetProperty("choice").GetString()!, choice.GetProperty("description").GetString()!));
                Assert.Equal(choices, option.Choices);
            }
        }

        Assert.Equal(template.RootElement.GetProperty("identity").GetString(), Catalog.Template.Identity);
        Assert.Equal(template.RootElement.GetProperty("shortName").GetString(), Catalog.Template.ShortName);
    }

    [Fact]
    public void Template_origin_records_every_option()
    {
        var origin = ProjectSpec.Read(Path.Combine(Repository.Template, "template-origin.json"));

        Assert.Equal(ProjectSpec.CurrentVersion, origin.SpecVersion);
        Assert.Equal(Catalog.Options.Select(option => option.Name), origin.Options.Keys);
        Assert.Equal(Catalog.Template.Identity, origin.Template);
        Assert.Equal(Catalog.Template.Package, origin.Package);
    }

    [Fact]
    public void The_checked_in_schema_is_the_one_the_catalog_generates()
    {
        var checkedIn = File.ReadAllText(Path.Combine(Repository.Root, "schemas", "ek-spec.schema.json")).ReplaceLineEndings("\n");

        // After a catalog change: dotnet run --project src/Erkmangelis.Ek -- schema > schemas/ek-spec.schema.json
        Assert.Equal(SpecSchema.Generate(Catalog), checkedIn);
    }

    [Fact]
    public void The_tool_and_the_template_package_have_the_same_version()
    {
        var package = System.Xml.Linq.XDocument.Load(Path.Combine(Repository.Root, "Erkmangelis.Templates.csproj"))
            .Descendants("Version").Single().Value;

        Assert.Equal(package, TemplatePackages.ToolVersion);
    }

    [Fact]
    public void Every_preset_is_supported_as_it_is()
    {
        foreach (var preset in Catalog.Presets)
        {
            var result = SpecResolver.Resolve(Catalog, "Acme.Notes", null, preset.Options);

            Assert.True(result.Succeeded, $"{preset.Name}: {string.Join("; ", result.Errors)}");
            Assert.Empty(result.Spec!.Implied);
            Assert.Equal(Catalog.Options.Select(option => option.Name).Order(), preset.Options.Keys.Order());
        }
    }

    [Fact]
    public void Preset_names_and_aliases_are_unique()
    {
        var names = Catalog.Presets.SelectMany(preset => preset.Aliases.Prepend(preset.Name)).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal("api-worker", Catalog.FindPreset("ek-worker")!.Name);
    }

    [Fact]
    public void A_rule_naming_an_unknown_option_value_is_rejected_on_load()
    {
        var modules = File.ReadAllText(Path.Combine(Repository.Catalog, "modules.json")).Replace("\"fallback\": {\n        \"db\": \"postgres\"", "\"fallback\": {\n        \"db\": \"mysql\"");
        var presets = File.ReadAllText(Path.Combine(Repository.Catalog, "presets.json"));

        var error = Assert.Throws<InvalidDataException>(() => Ek.Catalog.Load(modules, presets));
        Assert.Contains("mysql", error.Message);
    }
}
