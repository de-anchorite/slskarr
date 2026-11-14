namespace Slskarr.Search;

/// <summary>A normalised Newznab search request. Used only to build the slskd query and rank results, never to name releases.</summary>
public sealed record SearchRequest(string? Query, string? Author, string? Title, IReadOnlyList<int> Categories)
{
    public bool IsRss =>
        string.IsNullOrWhiteSpace(Query) && string.IsNullOrWhiteSpace(Author) && string.IsNullOrWhiteSpace(Title);
}
