using Slskarr.Profiles;

namespace Slskarr.Search;

/// <summary>
/// Identity of a search: <c>profile:sorted tokens</c>. Token order doesn't matter, so the Newznab tiers
/// <c>q=Dune Frank Herbert</c> and <c>author=Frank Herbert&amp;title=Dune</c> share one key.
/// </summary>
public sealed record SearchKey(string Value, IReadOnlySet<string> Tokens)
{
    public static SearchKey For(IMediaProfile p, SearchRequest r)
    {
        var tokens = NameMatch.Tokens(p.BuildQuery(r));
        return new SearchKey(p.Name + ":" + string.Join(' ', tokens.Order(StringComparer.Ordinal)), tokens);
    }

    /// <summary>Recovers the token set from a stored key value (the part after <c>profile:</c>).</summary>
    public static IReadOnlySet<string> TokensOf(string keyValue) =>
        keyValue[(keyValue.IndexOf(':') + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    // The token set is derived from Value, so Value alone defines equality.
    public bool Equals(SearchKey? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}
