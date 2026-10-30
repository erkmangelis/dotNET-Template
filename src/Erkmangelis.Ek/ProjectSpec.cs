using System.Text.Json;
using System.Text.Json.Nodes;

namespace Erkmangelis.Ek;

/// <summary>
/// A generation request: which template, which version, the project name and its options. The same
/// format is the generated project's template-origin.json, so a project's provenance is a spec that
/// reproduces it. Schema: schemas/ek-spec.schema.json (from <c>ek schema</c>).
/// </summary>
public sealed record ProjectSpec
{
    public const int CurrentVersion = 1;

    /// <summary>Null for the template-origin.json of template versions before 0.5, which had no spec version.</summary>
    public int? SpecVersion { get; init; }

    public string? Template { get; init; }

    public string? Package { get; init; }

    public string? Version { get; init; }

    public string? Name { get; init; }

    public string? Preset { get; init; }

    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    private static readonly string[] KnownProperties = ["$schema", "specVersion", "template", "package", "version", "name", "preset", "options"];

    public static ProjectSpec Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or IOException)
        {
            throw new SpecException($"{path}: {exception.Message}");
        }
    }

    public static ProjectSpec Parse(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new InvalidDataException("A spec must be a JSON object.");
        foreach (var unknown in node.Select(pair => pair.Key).Where(key => !KnownProperties.Contains(key)))
        {
            throw new InvalidDataException($"Unknown property '{unknown}'.");
        }

        var specVersion = node["specVersion"]?.GetValue<int>();
        if (specVersion > CurrentVersion)
        {
            throw new InvalidDataException($"specVersion {specVersion} is newer than this tool understands ({CurrentVersion}). Update ek.");
        }

        return new ProjectSpec
        {
            SpecVersion = specVersion,
            Template = node["template"]?.GetValue<string>(),
            Package = node["package"]?.GetValue<string>(),
            Version = node["version"]?.GetValue<string>(),
            Name = node["name"]?.GetValue<string>(),
            Preset = node["preset"]?.GetValue<string>(),
            Options = node["options"] is JsonObject options
                ? options.ToDictionary(pair => pair.Key, pair => Catalog.Value(pair.Value ?? throw new InvalidDataException($"Option '{pair.Key}' is null.")))
                : new Dictionary<string, string>(),
        };
    }

    /// <summary>Writes a resolved spec in the same shape as template-origin.json.</summary>
    public static string Write(Catalog catalog, ResolvedSpec spec, string version)
    {
        var options = new JsonObject();
        foreach (var option in catalog.Options)
        {
            var value = spec.Options[option.Name];
            options[option.Name] = option.IsBool ? JsonValue.Create(value == "true") : JsonValue.Create(value);
        }

        var node = new JsonObject
        {
            ["specVersion"] = CurrentVersion,
            ["template"] = catalog.Template.Identity,
            ["package"] = catalog.Template.Package,
            ["version"] = version,
            ["name"] = spec.Name,
            ["options"] = options,
        };
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}

public sealed class SpecException(string message) : Exception(message);
