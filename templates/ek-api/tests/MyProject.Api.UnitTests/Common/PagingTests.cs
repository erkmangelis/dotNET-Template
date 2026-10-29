namespace MyProject.Api.UnitTests.Common;

public sealed class PagingTests
{
    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(2, 20, 20)]
    [InlineData(5, 10, 40)]
    public void Skip_counts_the_items_on_earlier_pages(int page, int pageSize, int expected) =>
        Assert.Equal(expected, Paging.Skip(page, pageSize));

    [Fact]
    public void Largest_allowed_page_does_not_overflow() =>
        Assert.True(Paging.Skip(Paging.MaxPage, Paging.MaxPageSize) > 0);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(20, 1, false)]
    [InlineData(21, 2, true)]
    [InlineData(40, 2, true)]
    public void Total_pages_and_next_page_follow_the_total(long total, int totalPages, bool hasNextPage)
    {
        var page = new PagedResult<int>([], Page: 1, PageSize: 20, TotalCount: total);

        Assert.Equal(totalPages, page.TotalPages);
        Assert.Equal(hasNextPage, page.HasNextPage);
    }
}
