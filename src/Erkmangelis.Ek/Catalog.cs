using System.Text.Json.Nodes;

namespace Erkmangelis.Ek;

/// <summary>
/// The template's options, the rules between them and the presets, read from catalog/modules.json
/// and catalog/presets.json. Every rule the CLI applies lives here, not in code; a test checks the
/// options against the template's real symbols and the rules against the template's own output.
/// </summary>
public sealed class Catalog
{
    public required TemplateInfo Template { get; init; }

    public required IReadOnlyList<CatalogOption> Options { get; init; }

    public required IReadOnlyList<CatalogRule> Rules { get; init; }

    public required IReadOnlyList<Preset> Presets { get; init; }

    public static Catalog Embedded { get; } = Load(ReadResource("catalog/modules.json"), ReadResource("catalog/presets.json"));

    public CatalogOption? FindOption(string name) => Options.FirstOrDefault(option => option.Name == name);

    public Preset? FindPreset(string name) =>
        Presets.FirstOrDefault(preset => preset.Name == name || preset.Aliases.Contains(name));

    public bool IsTemplate(string name) => name == Template.Identity || name == Template.ShortName;

    public static Catalog Load(string modulesJson, string presetsJson)
    {
        var modules = JsonNode.Parse(modulesJson)!.AsObject();
        var template = modules["template"]!.AsObject();
        var catalog = new Catalog
        {
            Template = new TemplateInfo(Text(template, "identity"), Text(template, "shortName"), Text(template, "package")),
            Options = [.. modules["options"]!.AsArray().Select(node => ReadOption(node!.AsObject()))],
            Rules = [.. modules["rules"]!.AsArray().Select(node => ReadRule(node!.AsObject()))],
            Presets = [.. JsonNode.Parse(presetsJson)!["presets"]!.AsArray().Select(node => ReadPreset(node!.AsObject()))],
        };
        catalog.CheckConsistency();
        return catalog;
    }

    private void CheckConsistency()
    {
        void CheckValues(string where, string option, IEnumerable<string> values)
        {
            var known = FindOption(option) ?? throw new InvalidDataException($"{where} names an unknown option '{option}'.");
            foreach (var value in values.Where(value => !known.Allows(value)))
            {
                throw new InvalidDataException($"{where} gives option '{option}' an unknown value '{value}'.");
            }
        }

        foreach (var rule in Rules)
        {
            foreach (var (option, values) in rule.When.Concat(rule.Requires).Concat(rule.Excludes))
            {
                CheckValues($"Rule '{rule.Id}'", option, values);
            }

            foreach (var (option, value) in rule.Fallback)
            {
                CheckValues($"Rule '{rule.Id}'", option, [value]);
            }
        }

        foreach (var preset in Presets)
        {
            foreach (var (option, value) in preset.Options)
            {
                CheckValues($"Preset '{preset.Name}'", option, [value]);
            }
        }
    }

    private static CatalogOption ReadOption(JsonObject node)
    {
        var type = Text(node, "type");
        IReadOnlyList<Choice> choices = type switch
        {
            "bool" => [new Choice("false", "No."), new Choice("true", "Yes.")],
            "choice" => [.. node["choices"]!.AsArray().Select(choice => new Choice(Text(choice!.AsObject(), "value"), Text(choice.AsObject(), "description")))],
            _ => throw new InvalidDataException($"Option type '{type}' is not supported."),
        };
        return new CatalogOption(Text(node, "name"), type, Value(node["default"]!), Text(node, "question"), Text(node, "description"), choices);
    }

    private static CatalogRule ReadRule(JsonObject node) => new(
        Text(node, "id"),
        ValueSets(node["when"]),
        ValueSets(node["requires"]),
        ValueSets(node["excludes"]),
        node["fallback"]?.AsObject().ToDictionary(pair => pair.Key, pair => Value(pair.Value!)) ?? [],
        Text(node, "reason"));

    private static Preset ReadPreset(JsonObject node) => new(
        Text(node, "name"),
        node["aliases"]?.AsArray().Select(alias => alias!.GetValue<string>()).ToArray() ?? [],
        Text(node, "description"),
        node["options"]!.AsObject().ToDictionary(pair => pair.Key, pair => Value(pair.Value!)));

    private static Dictionary<string, string[]> ValueSets(JsonNode? node) =>
        node?.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.AsArray().Select(value => Value(value!)).ToArray()) ?? [];

    private static string Text(JsonObject node, string property) =>
        node[property]?.GetValue<string>() ?? throw new InvalidDataException($"The catalog is missing '{property}'.");

    /// <summary>Option values are kept as strings; booleans become "true" and "false", as <c>dotnet new</c> takes them.</summary>
    internal static string Value(JsonNode node) => node.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
        var kind => throw new InvalidDataException($"An option value must be a string or a boolean, not {kind}."),
    };

    private static string ReadResource(string name)
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The {name} resource is missing from the tool.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed record TemplateInfo(string Identity, string ShortName, string Package);

public sealed record Choice(string Value, string Description);

public sealed record CatalogOption(string Name, string Type, string Default, string Question, string Description, IReadOnlyList<Choice> Choices)
{
    public bool IsBool => Type == "bool";

    public bool Allows(string value) => Choices.Any(choice => choice.Value == value);
}

/// <summary>
/// When every option in <see cref="When"/> has one of the listed values, each option in
/// <see cref="Requires"/> must have one of its listed values (if it is not set, it takes the
/// <see cref="Fallback"/> value) and no option in <see cref="Excludes"/> may have one of its listed values.
/// </summary>
public sealed record CatalogRule(
    string Id,
    IReadOnlyDictionary<string, string[]> When,
    IReadOnlyDictionary<string, string[]> Requires,
    IReadOnlyDictionary<string, string[]> Excludes,
    IReadOnlyDictionary<string, string> Fallback,
    string Reason);

public sealed record Preset(string Name, IReadOnlyList<string> Aliases, string Description, IReadOnlyDictionary<string, string> Options);
