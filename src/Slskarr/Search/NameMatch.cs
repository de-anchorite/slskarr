using System.Globalization;
using System.Text;
using Slskarr.Profiles;
using Slskarr.Profiles.Music;

namespace Slskarr.Search;

public enum MatchBucket
{
    None = 0,
    Weak = 1,
    Partial = 2,
    Strong = 3,
}

/// <summary>Scores how well a parsed Soulseek name matches a request, in coarse buckets.</summary>
public static class NameMatch
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "the", "a", "an", "and", "of" };

    private const double PartialThreshold = 0.6;

    public static MatchBucket Score(ParsedName name, SearchRequest request)
    {
        if (request.IsRss)
        {
            return MatchBucket.None;
        }

        // Music requests: Artist is the author and Album (brackets removed, as in BuildQuery) the title.
        // Various Artists carries no author tokens.
        var music = !string.IsNullOrWhiteSpace(request.Artist) || !string.IsNullOrWhiteSpace(request.Album);
        var author = music
            ? (MusicProfile.IsVariousArtists(request.Artist?.Trim() ?? "") ? null : request.Artist)
            : request.Author;
        var title = music && !string.IsNullOrWhiteSpace(request.Album) ? MusicProfile.StripBrackets(request.Album) : request.Title;

        var authorTokens = Tokens(author);
        var titleTokens = Tokens(string.IsNullOrWhiteSpace(title) ? request.Query : title);
        var requestTokens = new HashSet<string>(authorTokens, StringComparer.Ordinal);
        requestTokens.UnionWith(Tokens(title));
        requestTokens.UnionWith(Tokens(request.Query));
        if (requestTokens.Count == 0)
        {
            return MatchBucket.None;
        }

        var hit = name.Author is null && name.Title is null
            ? Tokens(name.Stem)
            : Tokens(name.Author).Union(Tokens(name.Title), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        if (titleTokens.Count > 0
            && titleTokens.All(hit.Contains)
            && (authorTokens.Count == 0 || authorTokens.Any(hit.Contains)))
        {
            return MatchBucket.Strong;
        }

        var present = requestTokens.Count(hit.Contains);
        if (present == 0)
        {
            return MatchBucket.None;
        }

        return (double)present / requestTokens.Count >= PartialThreshold ? MatchBucket.Partial : MatchBucket.Weak;
    }

    public static IReadOnlySet<string> Tokens(string? s)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(s))
        {
            return tokens;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        foreach (var token in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!StopWords.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }
}
