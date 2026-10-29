using System.Text.Json;
using System.Xml.Linq;

namespace Erkmangelis.Templates.Tests;

public sealed class TemplatePackageTests
{
    [Fact]
    public void The_version_written_to_template_origin_matches_the_package_version()
    {
        var project = XDocument.Load(Path.Combine(RepositoryPaths.Root, "Erkmangelis.Templates.csproj"));
        var packageVersion = project.Descendants("Version").Single().Value;

        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Template, ".template.config", "template.json")));
        var templateVersion = template.RootElement
            .GetProperty("symbols").GetProperty("TemplateVersion")
            .GetProperty("parameters").GetProperty("value").GetString();

        Assert.Equal(packageVersion, templateVersion);
    }

    [Fact]
    public void Every_dependency_is_free_of_commercial_licences()
    {
        // Standing rule: no default dependency may need a commercial licence.
        string[] banned = ["MediatR", "AutoMapper", "MassTransit", "FluentAssertions", "Duende."];
        var packages = XDocument.Load(Path.Combine(RepositoryPaths.Template, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Select(element => element.Attribute("Include")!.Value);

        Assert.DoesNotContain(packages, package => banned.Any(name => package.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
    }
}
