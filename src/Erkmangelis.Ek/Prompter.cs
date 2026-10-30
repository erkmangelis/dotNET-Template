namespace Erkmangelis.Ek;

/// <summary>
/// The questions <c>ek new</c> asks for what the command line did not give. Each question offers
/// only the values the rules accept with the answers so far, so the result is always supported.
/// </summary>
public static class Prompter
{
    public static (string? Name, string? Preset) Ask(
        Catalog catalog, string? name, string? preset, Dictionary<string, string> options, bool optionsGiven)
    {
        while (string.IsNullOrWhiteSpace(name))
        {
            Console.Write("Project name (for example Acme.Notes): ");
            name = Console.ReadLine()?.Trim();
            if (name is null)
            {
                return (null, preset);
            }
        }

        if (preset is null && !optionsGiven)
        {
            var choices = catalog.Presets.Select(p => new Choice(p.Name, p.Description))
                .Append(new Choice("custom", "Answer a question per option."))
                .ToList();
            var answer = Choose("Start from a preset?", choices, "custom");
            preset = answer == "custom" ? null : answer;
        }

        var fixedByPreset = preset is not null ? catalog.FindPreset(preset)?.Options.Keys.ToHashSet() ?? [] : [];
        foreach (var option in catalog.Options.Where(option => !options.ContainsKey(option.Name) && !fixedByPreset.Contains(option.Name)))
        {
            var allowed = option.Choices
                .Where(choice => SpecResolver.Resolve(catalog, name, preset, new Dictionary<string, string>(options) { [option.Name] = choice.Value }).Succeeded)
                .ToList();
            if (allowed.Count == 1)
            {
                options[option.Name] = allowed[0].Value;
                Console.WriteLine($"{option.Question} {allowed[0].Value} (the only choice with your answers)");
                continue;
            }

            // Default to what the rules would pick on their own, so pressing Enter changes nothing.
            var current = SpecResolver.Resolve(catalog, name, preset, options).Spec?.Options[option.Name] ?? option.Default;
            var fallback = allowed.Any(choice => choice.Value == current) ? current : allowed[0].Value;
            options[option.Name] = Choose(option.Question, allowed, fallback);
        }

        return (name, preset);
    }

    private static string Choose(string question, IReadOnlyList<Choice> choices, string fallback)
    {
        Console.WriteLine(question);
        for (var index = 0; index < choices.Count; index++)
        {
            var marker = choices[index].Value == fallback ? "*" : " ";
            Console.WriteLine($" {marker}{index + 1}. {choices[index].Value,-11} {choices[index].Description}");
        }

        while (true)
        {
            Console.Write($"Choose 1-{choices.Count} or a value [{fallback}]: ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer))
            {
                return fallback;
            }

            if (int.TryParse(answer, out var number) && number >= 1 && number <= choices.Count)
            {
                return choices[number - 1].Value;
            }

            if (choices.FirstOrDefault(choice => string.Equals(choice.Value, answer, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match.Value;
            }
        }
    }
}
