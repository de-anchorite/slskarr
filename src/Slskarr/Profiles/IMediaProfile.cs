using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Profiles;

public sealed record ReleaseCandidate(
    string Profile,
    string Username,
    string RemotePath,
    long Size,
    ParsedName Name,
    string ReleaseTitle,
    bool FreeSlot,
    int QueueLength,
    int UploadSpeed);

/// <summary>A media type Slskarr can search for and download (v1: ebooks).</summary>
public interface IMediaProfile
{
    string Name { get; }

    IReadOnlyList<int> Categories { get; }

    string SabCategory { get; }

    /// <summary>Ranking criteria, in priority order, handed to <see cref="Ranker"/>.</summary>
    IReadOnlyList<string> Ranking { get; }

    bool Handles(SearchRequest r);

    string BuildQuery(SearchRequest r);

    IReadOnlyList<ReleaseCandidate> ToCandidates(IEnumerable<SlskdSearchResponse> responses);

    /// <summary>Position of a format in the preference order; lower is better, unknown formats rank last.</summary>
    int FormatRank(string format);
}
