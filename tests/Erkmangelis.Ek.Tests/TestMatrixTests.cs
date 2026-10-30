namespace Erkmangelis.Ek.Tests;

public sealed class TestMatrixTests
{
    private static readonly Catalog Catalog = Ek.Catalog.Embedded;

    [Fact]
    public void The_matrix_covers_every_supported_pair_of_values()
    {
        var names = Catalog.Options.Select(option => option.Name).ToArray();
        var supportedPairs = SpecResolver.SupportedCombinations(Catalog).SelectMany(combination => TestMatrix.Pairs(names, combination)).ToHashSet();
        var rows = TestMatrix.Build(Catalog);

        var coveredPairs = rows.SelectMany(row => TestMatrix.Pairs(names, row.Options)).ToHashSet();

        Assert.Empty(supportedPairs.Except(coveredPairs));
    }

    [Fact]
    public void The_matrix_has_every_preset_and_only_supported_rows()
    {
        var rows = TestMatrix.Build(Catalog);

        Assert.Equal(Catalog.Presets.Select(preset => $"preset {preset.Name}"), rows.Take(Catalog.Presets.Count).Select(row => row.Name));
        Assert.All(rows, row => Assert.True(SpecResolver.Resolve(Catalog, "Acme.Notes", null, row.Options).Succeeded, row.Name));
        Assert.Equal(rows.Count, rows.Select(row => string.Join(",", row.Options.Values)).Distinct().Count());
    }

    [Fact]
    public void The_matrix_is_much_smaller_than_every_combination_and_stable()
    {
        var all = SpecResolver.SupportedCombinations(Catalog).Count();
        var rows = TestMatrix.Build(Catalog);

        Assert.True(rows.Count < all / 20, $"{rows.Count} rows for {all} combinations");
        Assert.Equal(rows.Select(row => row.Name), TestMatrix.Build(Catalog).Select(row => row.Name));
        Assert.Equal(rows.Select(row => row.Options), TestMatrix.Build(Catalog).Select(row => row.Options));
    }
}
