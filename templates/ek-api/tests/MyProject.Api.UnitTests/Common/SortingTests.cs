namespace MyProject.Api.UnitTests.Common;

public sealed class SortingTests
{
    private static readonly SortFields<Item> _fields = new SortFields<Item>()
        .Add("name", item => item.Name)
        .Add("rank", item => item.Rank);

    private static readonly Item[] _items = [new("b", 1), new("a", 2), new("c", 1)];

    [Theory]
    [InlineData("name", "a,b,c")]
    [InlineData("-name", "c,b,a")]
    [InlineData("rank,-name", "c,b,a")]
    [InlineData("-rank, NAME", "a,b,c")]
    public void Allowed_fields_sort_in_the_given_order(string sort, string expected)
    {
        Assert.True(_fields.IsValid(sort));

        var sorted = _fields.Apply(_items.AsQueryable(), sort).Select(item => item.Name);

        Assert.Equal(expected, string.Join(',', sorted));
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret")]
    [InlineData("name,name")]
    [InlineData("name,-name")]
    [InlineData("name,rank,name,rank")]
    [InlineData("--name")]
    [InlineData("name,")]
    public void Anything_else_is_refused(string sort) => Assert.False(_fields.IsValid(sort));

    private sealed record Item(string Name, int Rank);
}
