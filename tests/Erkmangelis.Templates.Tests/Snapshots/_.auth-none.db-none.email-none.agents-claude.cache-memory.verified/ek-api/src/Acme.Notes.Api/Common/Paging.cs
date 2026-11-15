namespace Acme.Notes.Api.Common;

/// <summary>Limits shared by every paged endpoint.</summary>
internal static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;

    public static int Skip(int page, int pageSize) => (page - 1) * pageSize;
}

/// <summary>One page of results, with what a client needs to fetch the next one.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public PagedResult<TOut> Map<TOut>(Func<T, TOut> map) => new([.. Items.Select(map)], Page, PageSize, TotalCount);
}

/// <summary>
/// One page of results without the total count, which costs a full scan on a large table. Stores
/// read one item more than the page holds to tell whether another page follows.
/// </summary>
public sealed record PageSlice<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasNextPage)
{
    /// <summary>Builds a page from up to <paramref name="pageSize"/> + 1 items read from the store.</summary>
    internal static PageSlice<T> FromOneExtra(List<T> items, int page, int pageSize) =>
        new([.. items.Take(pageSize)], page, pageSize, items.Count > pageSize);

    public PageSlice<TOut> Map<TOut>(Func<T, TOut> map) => new([.. Items.Select(map)], Page, PageSize, HasNextPage);
}
