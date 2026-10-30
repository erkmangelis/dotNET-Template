using System.Text.RegularExpressions;

namespace Erkmangelis.Ek;

/// <summary>A spec with every option decided, ready for <c>dotnet new</c>.</summary>
public sealed record ResolvedSpec(string Name, IReadOnlyDictionary<string, string> Options, IReadOnlyList<ImpliedValue> Implied)
{
    /// <summary>The <c>dotnet new</c> arguments after the template name: every option, in catalog order.</summary>
    public IReadOnlyList<string> TemplateArguments(Catalog catalog) =>
        [.. catalog.Options.SelectMany(option => new[] { $"--{option.Name}", Options[option.Name] })];
}

/// <summary>An option the rules set because another option needed it.</summary>
public sealed record ImpliedValue(string Option, string Value, CatalogRule Rule);

public sealed record ResolveResult(ResolvedSpec? Spec, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Spec is not null;
}

/// <summary>
/// Applies a preset, the explicit options and the catalog rules. An option nobody set takes the
/// value a rule needs, or else its default; an explicit value that breaks a rule is rejected with
/// the rule's reason. Preset values count as explicit, so a preset is never silently changed.
/// </summary>
public static partial class SpecResolver
{
    public static ResolveResult Resolve(Catalog catalog, string? name, string? preset, IReadOnlyDictionary<string, string> options)
    {
        var errors = new List<string>();
        var values = new Dictionary<string, string>();

        if (name is not null && !ValidName().IsMatch(name))
        {
            errors.Add($"'{name}' is not a valid project name: use letters, digits, '.' and '_', starting with a letter.");
        }

        if (preset is not null)
        {
            var found = catalog.FindPreset(preset);
            if (found is null)
            {
                errors.Add($"Unknown preset '{preset}'. Presets: {string.Join(", ", catalog.Presets.Select(p => p.Name))}.");
            }
            else
            {
                foreach (var (option, value) in found.Options)
                {
                    values[option] = value;
                }
            }
        }

        foreach (var (option, value) in options)
        {
            var known = catalog.FindOption(option);
            if (known is null)
            {
                errors.Add($"Unknown option '{option}'. Options: {string.Join(", ", catalog.Options.Select(o => o.Name))}.");
            }
            else if (!known.Allows(value))
            {
                errors.Add($"'{value}' is not a value of {option}. Choose {string.Join(", ", known.Choices.Select(c => c.Value))}.");
            }
            else
            {
                values[option] = value;
            }
        }

        if (errors.Count > 0)
        {
            return new ResolveResult(null, errors);
        }

        var implied = new List<ImpliedValue>();
        string Effective(string option) => values.TryGetValue(option, out var value) ? value : catalog.FindOption(option)!.Default;
        bool Matches(IReadOnlyDictionary<string, string[]> condition) => condition.All(pair => pair.Value.Contains(Effective(pair.Key)));

        // A fallback can switch on another rule (the worker needs Hangfire, Hangfire needs a
        // database), so repeat until nothing changes. Each pass sets at least one more option.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var rule in catalog.Rules.Where(rule => Matches(rule.When)))
            {
                var trigger = string.Join(" and ", rule.When.Keys.Select(option => $"{option} {Effective(option)}"));
                foreach (var (option, allowed) in rule.Requires)
                {
                    if (values.TryGetValue(option, out var value))
                    {
                        if (!allowed.Contains(value))
                        {
                            errors.Add(Conflict(rule, trigger, option, value, $"needs {option} {Alternatives(allowed)}", implied));
                        }
                    }
                    else if (rule.Fallback.TryGetValue(option, out var fallback))
                    {
                        values[option] = fallback;
                        implied.Add(new ImpliedValue(option, fallback, rule));
                        changed = true;
                    }
                    else if (!allowed.Contains(Effective(option)))
                    {
                        errors.Add(Conflict(rule, trigger, option, Effective(option), $"needs {option} {Alternatives(allowed)}", implied));
                    }
                }

                foreach (var (option, excluded) in rule.Excludes)
                {
                    if (excluded.Contains(Effective(option)))
                    {
                        errors.Add(Conflict(rule, trigger, option, Effective(option), $"cannot be combined with {option} {Alternatives(excluded)}", implied));
                    }
                }
            }

            if (errors.Count > 0)
            {
                return new ResolveResult(null, [.. errors.Distinct()]);
            }
        }

        var resolved = catalog.Options.ToDictionary(option => option.Name, option => Effective(option.Name));
        return new ResolveResult(new ResolvedSpec(name ?? "", resolved, implied), []);
    }

    public static ResolveResult Resolve(Catalog catalog, ProjectSpec spec, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (spec.Template is not null && !catalog.IsTemplate(spec.Template))
        {
            return new ResolveResult(null, [$"This tool generates {catalog.Template.ShortName} ({catalog.Template.Identity}), not '{spec.Template}'."]);
        }

        var options = new Dictionary<string, string>(spec.Options);
        foreach (var (option, value) in overrides ?? new Dictionary<string, string>())
        {
            options[option] = value;
        }

        return Resolve(catalog, spec.Name, spec.Preset, options);
    }

    /// <summary>Every combination of explicit values that the rules accept as it is.</summary>
    public static IEnumerable<IReadOnlyDictionary<string, string>> SupportedCombinations(Catalog catalog)
    {
        IEnumerable<Dictionary<string, string>> combinations = [new Dictionary<string, string>()];
        foreach (var option in catalog.Options)
        {
            combinations = combinations.SelectMany(partial => option.Choices.Select(choice => new Dictionary<string, string>(partial) { [option.Name] = choice.Value }));
        }

        return combinations.Where(combination => Resolve(catalog, null, null, combination).Succeeded);
    }

    private static string Conflict(CatalogRule rule, string trigger, string option, string value, string need, List<ImpliedValue> implied)
    {
        var because = implied.FirstOrDefault(item => rule.When.ContainsKey(item.Option)) is { } chain
            ? $" ({chain.Option} {chain.Value} was set because {chain.Rule.Reason.TrimEnd('.').ToLowerInvariant()})"
            : "";
        return $"{trigger}{because} {need}, but {option} is {value}. {rule.Reason} [{rule.Id}]";
    }

    private static string Alternatives(string[] values) => values.Length == 1 ? values[0] : $"{string.Join(", ", values[..^1])} or {values[^1]}";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)*$")]
    private static partial Regex ValidName();
}
