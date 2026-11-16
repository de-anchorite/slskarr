using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Profiles.Ebook;

public sealed partial class EbookProfile(IOptions<SlskarrOptions> options, EbookNameParser parser) : IMediaProfile
{
    private readonly EbookProfileOptions _options = options.Value.Ebook;

    public string Name => "ebook";

    public IReadOnlyList<int> Categories { get; } = [7000, 7020];

    public string SabCategory => _options.SabCategory;

    public IReadOnlyList<string> Ranking => _options.Ranking;

    [GeneratedRegex(@"[:;,!?""'()\[\]]", RegexOptions.CultureInvariant)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    public bool CoalesceBySuperset => true;

    public NewznabCategory CapsCategory { get; } = new(7000, "Books", [(7020, "Books/EBook")]);

    public IReadOnlyList<int> ItemCategories(HitEntity hit) => [7000, 7020];

    public IReadOnlyList<(string Name, string Value)> ItemAttributes(HitEntity hit)
    {
        var attrs = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(hit.ParsedAuthor))
        {
            attrs.Add(("author", hit.ParsedAuthor));
        }
        if (!string.IsNullOrWhiteSpace(hit.ParsedTitle))
        {
            attrs.Add(("booktitle", hit.ParsedTitle));
        }
        return attrs;
    }

    public bool Handles(SearchRequest r) => r.Function switch
    {
        "book" or null or "search" => r.Categories.Count == 0 || r.Categories.Any(c => c is >= 7000 and < 8000),
        _ => false,
    };

    public string BuildQuery(SearchRequest r)
    {
        var raw = !string.IsNullOrWhiteSpace(r.Query)
            ? r.Query
            : string.Join(' ', new[] { r.Author, r.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return WhitespaceRegex().Replace(PunctuationRegex().Replace(raw, " "), " ").Trim();
    }

    public IReadOnlyList<ReleaseCandidate> ToCandidates(IEnumerable<SlskdSearchResponse> responses)
    {
        var extensions = new HashSet<string>(_options.Extensions, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string, string)>();
        var result = new List<ReleaseCandidate>();
        foreach (var response in responses)
        {
            foreach (var file in response.Files)
            {
                var ext = SoulseekPath.Extension(file.Filename);
                if (!extensions.Contains(ext))
                {
                    continue;
                }
                if (file.Size < _options.MinSizeBytes || file.Size > _options.MaxSizeBytes)
                {
                    continue;
                }
                if (!seen.Add((response.Username, file.Filename)))
                {
                    continue;
                }
                var name = parser.Parse(file.Filename);
                result.Add(new ReleaseCandidate(
                    this.Name, response.Username, file.Filename, file.Size, name, ReleaseTitle(name),
                    response.HasFreeUploadSlot, response.QueueLength, response.UploadSpeed,
                    [new CandidateFile(file.Filename, file.Size, "", file.BitRate, file.BitDepth, file.SampleRate, file.Length, file.IsVariableBitRate)]));
            }
        }
        return result;
    }

    public int FormatRank(string format)
    {
        var pref = _options.FormatPreference;
        var index = Array.FindIndex(pref, f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? pref.Length : index;
    }

    private static string ReleaseTitle(ParsedName n)
    {
        var format = n.Format.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(n.Author) || string.IsNullOrWhiteSpace(n.Title))
        {
            return $"{n.Stem} [{format}]";
        }
        var year = n.Year is { } y ? $" ({y})" : "";
        return $"{n.Author} - {n.Title}{year} [{format}]";
    }
}
