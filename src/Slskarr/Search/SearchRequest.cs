namespace Slskarr.Search;

/// <summary>A normalised Newznab search request. Used only to build the slskd query and rank results, never to name releases.</summary>
/// <remarks><c>Function</c> is the Newznab <c>t</c> parameter (<c>search</c>, <c>book</c>, <c>music</c>).</remarks>
public sealed record SearchRequest(
    string? Query,
    string? Author,
    string? Title,
    IReadOnlyList<int> Categories,
    string? Artist = null,
    string? Album = null,
    string? Function = null)
{
    public bool IsRss =>
        string.IsNullOrWhiteSpace(Query) && string.IsNullOrWhiteSpace(Author) && string.IsNullOrWhiteSpace(Title)
        && string.IsNullOrWhiteSpace(Artist) && string.IsNullOrWhiteSpace(Album);
}
