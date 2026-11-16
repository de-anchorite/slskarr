using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Profiles.Music;

/// <summary>
/// Music (Lidarr). A release is one folder from one peer in one audio format; disc sub-folders are joined.
/// Cue/image rips are dropped: Lidarr cannot import them and Slskarr never splits files.
/// </summary>
public sealed partial class MusicProfile(IOptions<SlskarrOptions> options) : IMediaProfile
{
    private static readonly HashSet<string> Lossless =
        new(["flac", "alac", "wav", "aiff", "aif", "ape", "wv"], StringComparer.OrdinalIgnoreCase);

    // A cue sheet with a single audio file this long (seconds) is a whole-album image.
    private const int ImageRipMinLengthSeconds = 1200;

    private readonly MusicProfileOptions _options = options.Value.Music;

    public string Name => "music";

    public IReadOnlyList<int> Categories { get; } = [3000, 3010, 3040];

    public string SabCategory => _options.SabCategory;

    public bool CoalesceBySuperset => false;

    public NewznabCategory CapsCategory { get; } =
        new(3000, "Audio", [(3010, "Audio/MP3"), (3040, "Audio/Lossless")]);

    public IReadOnlyList<string> Ranking => _options.Ranking;

    [GeneratedRegex(@"[:;,!?""'()\[\]]", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"^(cd|disc|disk)\s*\d+\b.*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolderRegex();

    public IReadOnlyList<int> ItemCategories(HitEntity hit) =>
        [3000, Lossless.Contains(hit.Format) ? 3040 : 3010];

    public IReadOnlyList<(string Name, string Value)> ItemAttributes(HitEntity hit)
    {
        var attrs = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(hit.ParsedAuthor))
        {
            attrs.Add(("artist", hit.ParsedAuthor));
        }
        if (!string.IsNullOrWhiteSpace(hit.ParsedTitle))
        {
            attrs.Add(("album", hit.ParsedTitle));
        }
        return attrs;
    }

    public bool Handles(SearchRequest r) => r.Function switch
    {
        "music" => true,
        null or "search" => r.Categories.Any(c => c is >= 3000 and < 4000 and not 3030),
        _ => false,
    };

    public string BuildQuery(SearchRequest r)
    {
        string raw;
        if (!string.IsNullOrWhiteSpace(r.Artist) || !string.IsNullOrWhiteSpace(r.Album))
        {
            var artist = r.Artist?.Trim() ?? "";
            var album = StripBrackets(r.Album ?? "");
            if (IsVariousArtists(artist) || artist.Equals(album, StringComparison.OrdinalIgnoreCase))
            {
                // Self-titled albums search the name once (tier 2 collapses "W W yyyy" to match).
                artist = "";
            }
            raw = string.Join(' ', new[] { artist, album }.Where(s => s.Length > 0));
            return Clean(raw);
        }

        raw = DropLeadingVariousArtists(StripBrackets(r.Query ?? ""));
        return CollapseSelfTitled(Clean(raw));
    }

    private static string Clean(string raw) => WhitespaceRegex().Replace(PunctuationRegex().Replace(raw, " "), " ").Trim();

    /// <summary>
    /// Lidarr's tier 2 for a self-titled album is <c>q=W W yyyy</c> (artist, album, year; <c>0</c> when the year
    /// is unknown) while tier 1 sends <c>artist=W&amp;album=W</c> and the year separately (ignored). Collapsing to
    /// <c>W</c> keeps one search key. The trailing year is required: a bare <c>Duran Duran</c> is a name, not a tier 2.
    /// </summary>
    private static string CollapseSelfTitled(string q)
    {
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || !IsLidarrYear(words[^1]))
        {
            return q;
        }
        var n = words.Length - 1;
        if (n % 2 != 0)
        {
            return q;
        }
        var half = n / 2;
        for (var i = 0; i < half; i++)
        {
            if (!words[i].Equals(words[half + i], StringComparison.OrdinalIgnoreCase))
            {
                return q;
            }
        }
        return string.Join(' ', words[..half]);
    }

    private static bool IsLidarrYear(string w) => w == "0" || (w.Length == 4 && w.All(char.IsAsciiDigit));

    // Lidarr sends "Various Artists" as "VA" in both tiers (artist=VA&album=X, then q=VA X); the artist is
    // dropped from the query, so tier 2 must drop it too for the two tiers to share one search key.
    private static string DropLeadingVariousArtists(string q)
    {
        var rest = LeadingVariousArtistsRegex().Replace(q, "");
        return rest.Length > 0 ? rest : q;
    }

    [GeneratedRegex(@"^\s*(?:various\s+artists|va)\b\s*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingVariousArtistsRegex();

    /// <summary>
    /// Removes bracketed segments; when that leaves nothing, keeps the text so "[Untitled]" still searches.
    /// Known, accepted mismatch: artist=Prince&amp;album=[Untitled] keys as "prince untitled" while the tier-2
    /// q=Prince [Untitled] keys without it. Brackets only reach us with Lidarr's "raw" text engine.
    /// </summary>
    internal static string StripBrackets(string text)
    {
        var stripped = WhitespaceRegex().Replace(BracketedRegex().Replace(text, " "), " ").Trim();
        return stripped.Length > 0 ? stripped : text.Trim();
    }

    internal static bool IsVariousArtists(string artist) =>
        artist.Equals("various artists", StringComparison.OrdinalIgnoreCase)
        || artist.Equals("va", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ReleaseCandidate> ToCandidates(IEnumerable<SlskdSearchResponse> responses)
    {
        var extensions = new HashSet<string>(_options.Extensions, StringComparer.OrdinalIgnoreCase);
        var result = new List<ReleaseCandidate>();
        foreach (var response in responses)
        {
            // folder path -> files, with disc folders folded into their parent.
            var groups = new Dictionary<string, List<(SlskdFile File, string Subdir)>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in response.Files)
            {
                if (!seen.Add(file.Filename))
                {
                    continue;
                }
                var folder = Directory(file.Filename);
                var subdir = "";
                var segments = SoulseekPath.Segments(folder);
                if (segments.Count > 1 && DiscFolderRegex().IsMatch(segments[^1]))
                {
                    subdir = segments[^1];
                    folder = Directory(folder);
                }
                if (!groups.TryGetValue(folder, out var list))
                {
                    groups[folder] = list = [];
                }
                list.Add((file, subdir));
            }

            foreach (var (folder, files) in groups)
            {
                AddCandidates(result, response, folder, files, extensions);
            }
        }
        return result;
    }

    private void AddCandidates(
        List<ReleaseCandidate> result,
        SlskdSearchResponse response,
        string folder,
        List<(SlskdFile File, string Subdir)> files,
        HashSet<string> extensions)
    {
        var audio = files.Where(f => extensions.Contains(SoulseekPath.Extension(f.File.Filename))).ToList();
        if (audio.Count == 0 || IsCueRip(files.Select(f => f.File).ToList(), audio.Select(f => f.File).ToList()))
        {
            return;
        }

        var folderName = SoulseekPath.FileName(folder);
        foreach (var byFormat in audio.GroupBy(f => SoulseekPath.Extension(f.File.Filename)))
        {
            var candidateFiles = byFormat
                .OrderBy(f => f.Subdir, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.File.Filename, StringComparer.OrdinalIgnoreCase)
                .Select(f => new CandidateFile(
                    f.File.Filename, f.File.Size, f.Subdir, f.File.BitRate, f.File.BitDepth, f.File.SampleRate, f.File.Length, f.File.IsVariableBitRate))
                .ToList();
            var size = candidateFiles.Sum(f => f.Size);
            if (size < _options.MinSizeBytes || size > _options.MaxSizeBytes)
            {
                continue;
            }

            var name = MusicNameParser.Parse(folder) with { Format = byFormat.Key.ToUpperInvariant() };
            var tag = MusicQuality.Tag(byFormat.Key, candidateFiles, folderName);
            result.Add(new ReleaseCandidate(
                Name, response.Username, folder, size, name, MusicTitle.Build(name, tag),
                response.HasFreeUploadSlot, response.QueueLength, response.UploadSpeed, candidateFiles));
        }
    }

    private static bool IsCueRip(List<SlskdFile> all, List<SlskdFile> audio)
    {
        var cue = all.Count(f => SoulseekPath.Extension(f.Filename) == "cue");
        if (all.Any(f => SoulseekPath.Extension(f.Filename) == "bin"))
        {
            return true;
        }
        return cue >= 1 && (audio.Count <= cue || audio.Any(f => f.Length > ImageRipMinLengthSeconds));
    }

    private static string Directory(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i <= 0 ? "" : path[..i];
    }

    public int FormatRank(string format)
    {
        var pref = _options.FormatPreference;
        var index = Array.FindIndex(pref, f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? pref.Length : index;
    }
}
