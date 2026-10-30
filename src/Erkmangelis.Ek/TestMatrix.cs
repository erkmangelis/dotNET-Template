namespace Erkmangelis.Ek;

/// <summary>
/// The CI acceptance matrix: every preset, plus supported combinations chosen so that every pair
/// of option values that can occur together occurs in at least one of them (pairwise coverage).
/// The choice is greedy and deterministic, so the matrix only changes when the catalog does.
/// </summary>
public static class TestMatrix
{
    public sealed record Row(string Name, IReadOnlyDictionary<string, string> Options);

    public static IReadOnlyList<Row> Build(Catalog catalog)
    {
        var rows = catalog.Presets
            .Select(preset => new Row($"preset {preset.Name}", SpecResolver.Resolve(catalog, null, preset.Name, new Dictionary<string, string>()).Spec!.Options))
            .ToList();
        var pairwise = Pairwise(catalog, rows.Select(row => row.Options));
        rows.AddRange(pairwise.Select((options, index) => new Row($"pairwise {index + 1:00}", options)));
        return rows;
    }

    /// <summary>Combinations that cover every pair the <paramref name="covered"/> rows (the presets) do not.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Pairwise(Catalog catalog, IEnumerable<IReadOnlyDictionary<string, string>> covered)
    {
        var candidates = SpecResolver.SupportedCombinations(catalog).ToList();
        var names = catalog.Options.Select(option => option.Name).ToArray();
        var uncovered = candidates.SelectMany(combination => Pairs(names, combination)).ToHashSet();
        uncovered.ExceptWith(covered.SelectMany(combination => Pairs(names, combination)));
        var chosen = new List<IReadOnlyDictionary<string, string>>();
        while (uncovered.Count > 0)
        {
            // The first candidate covering the most uncovered pairs; ties keep catalog order.
            var best = candidates.MaxBy(combination => Pairs(names, combination).Count(uncovered.Contains))!;
            uncovered.ExceptWith(Pairs(names, best));
            chosen.Add(best);
        }

        return chosen;
    }

    public static IEnumerable<string> Pairs(string[] names, IReadOnlyDictionary<string, string> combination)
    {
        for (var first = 0; first < names.Length; first++)
        {
            for (var second = first + 1; second < names.Length; second++)
            {
                yield return $"{names[first]}={combination[names[first]]};{names[second]}={combination[names[second]]}";
            }
        }
    }
}
