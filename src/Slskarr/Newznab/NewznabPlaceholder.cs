namespace Slskarr.Newznab;

/// <summary>
/// The single item the RSS feed returns while Slskarr has no stored hits yet.
/// </summary>
/// <remarks>
/// Chaptarr/Readarr test an indexer by fetching its RSS ("recent") feed, and Prowlarr by sending
/// <c>t=search</c> with no query. Both refuse to save the indexer when that feed has no items, and a
/// fresh Slskarr has nothing stored. The placeholder lets the test pass. Its title does not parse as a
/// book, so RSS sync rejects it, and <c>t=get</c> for its id answers "No such item". It disappears once
/// real hits are stored.
/// </remarks>
public static class NewznabPlaceholder
{
    public const string Id = "slskarr-placeholder";
    public const string Title = "Slskarr placeholder (no Soulseek results stored yet)";
    public const long Size = 1024;
}
