using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Profiles;

/// <summary>One file of a release; <c>Subdir</c> is the disc/sub-folder below the release folder ("" for none).</summary>
public sealed record CandidateFile(
    string RemotePath,
    long Size,
    string Subdir,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    bool? IsVbr);

/// <summary>A downloadable release. <c>Size</c> is the sum of <c>Files</c>.</summary>
public sealed record ReleaseCandidate(
    string Profile,
    string Username,
    string RemotePath,
    long Size,
    ParsedName Name,
    string ReleaseTitle,
    bool FreeSlot,
    int QueueLength,
    int UploadSpeed,
    IReadOnlyList<CandidateFile> Files);

/// <summary>A media type Slskarr can search for and download (v1: ebooks).</summary>
public interface IMediaProfile
{
    string Name { get; }

    IReadOnlyList<int> Categories { get; }

    string SabCategory { get; }

    /// <summary>True when a finished release is a folder (music), false when it is one file (ebooks).</summary>
    bool StorageIsFolder { get; }

    /// <summary>Ranking criteria, in priority order, handed to <see cref="Ranker"/>.</summary>
    IReadOnlyList<string> Ranking { get; }

    bool Handles(SearchRequest r);

    string BuildQuery(SearchRequest r);

    IReadOnlyList<ReleaseCandidate> ToCandidates(IEnumerable<SlskdSearchResponse> responses);

    /// <summary>Position of a format in the preference order; lower is better, unknown formats rank last.</summary>
    int FormatRank(string format);
}
