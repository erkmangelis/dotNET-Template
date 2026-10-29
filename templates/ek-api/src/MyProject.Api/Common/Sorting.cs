using System.Linq.Expressions;

namespace MyProject.Api.Common;

/// <summary>
/// The fields a list endpoint lets clients sort by. A sort is a comma-separated list of field names,
/// each with an optional leading <c>-</c> for descending, for example <c>-createdAt,title</c>.
/// Only names added here are accepted, so a client can never order by a column you did not choose.
/// </summary>
internal sealed class SortFields<T>
{
    /// <summary>More terms than this are refused; each one costs the database work.</summary>
    public const int MaxTerms = 3;

    private delegate IOrderedQueryable<T> Orderer(IQueryable<T> query, bool descending, bool first);

    private readonly Dictionary<string, Orderer> _fields = new(StringComparer.OrdinalIgnoreCase);

    public SortFields<T> Add<TKey>(string name, Expression<Func<T, TKey>> key)
    {
        _fields[name] = (query, descending, first) => (first, descending) switch
        {
            (true, false) => query.OrderBy(key),
            (true, true) => query.OrderByDescending(key),
            (false, false) => ((IOrderedQueryable<T>)query).ThenBy(key),
            (false, true) => ((IOrderedQueryable<T>)query).ThenByDescending(key),
        };
        return this;
    }

    /// <summary>The accepted field names, for error messages and documentation.</summary>
    public IEnumerable<string> Names => _fields.Keys;

    public bool IsValid(string? sort) => Parse(sort) is not null;

    /// <summary>Orders <paramref name="query"/> by <paramref name="sort"/>. Call <see cref="IsValid"/> first.</summary>
    public IOrderedQueryable<T> Apply(IQueryable<T> query, string sort)
    {
        var terms = Parse(sort) ?? throw new ArgumentException($"'{sort}' is not a valid sort.", nameof(sort));
        var ordered = terms[0].Order(query, terms[0].Descending, true);
        foreach (var term in terms.Skip(1))
        {
            ordered = term.Order(ordered, term.Descending, false);
        }

        return ordered;
    }

    private List<(Orderer Order, bool Descending)>? Parse(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return null;
        }

        var parts = sort.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > MaxTerms)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<(Orderer, bool)>(parts.Length);
        foreach (var part in parts)
        {
            var descending = part.StartsWith('-');
            var name = descending ? part[1..] : part;
            if (!seen.Add(name) || !_fields.TryGetValue(name, out var order))
            {
                return null;
            }

            terms.Add((order, descending));
        }

        return terms;
    }
}
