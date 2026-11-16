using System.Text.RegularExpressions;
using Slskarr.Soulseek;

namespace Slskarr.Profiles.Music;

/// <summary>
/// Builds the quality tag Lidarr reads from the release title. Lidarr only knows the codec and bit rate from
/// the title, so the tag is derived from slskd file attributes (or folder-name markers when there are none).
/// </summary>
public static partial class MusicQuality
{
    // Buckets Lidarr's title parser maps per codec (QualityParser.cs): MP3 CBR has no 224; AAC only 192/256/320
    // (anything else reads as AAC-VBR, so the closest mapped bucket is used); Vorbis/Opus map Q5-Q10 = 160..500.
    private static readonly int[] Mp3Bitrates = [96, 128, 160, 192, 256, 320];
    private static readonly int[] AacBitrates = [192, 256, 320];
    private static readonly int[] VorbisBitrates = [160, 192, 224, 256, 320, 500];

    [GeneratedRegex(@"24[-._ ]?bit", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Marker24Regex();

    [GeneratedRegex(@"(?<![\w])(96|128|160|192|224|256|320)(?:[ ]?kbps)?(?![\w])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MarkerBitrateRegex();

    [GeneratedRegex(@"(?<![\w])V([02])(?![\w])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MarkerVbrRegex();

    public static string Tag(string format, IReadOnlyList<CandidateFile> files, string folderName)
    {
        var ext = format.Trim().TrimStart('.').ToLowerInvariant();
        var same = files.Where(f => SoulseekPath.Extension(f.RemotePath) == ext).ToList();
        var set = same.Count > 0 ? same : files.ToList();

        switch (ext)
        {
            case "flac":
                return Lossless("FLAC", set, folderName);
            case "alac":
                return Lossless("ALAC", set, folderName);
            case "mp3":
                return Mp3(set, folderName);
            case "m4a" or "aac":
                return Aac(set, folderName);
            case "ogg":
                return Vorbis("OGG", set);
            case "opus":
                return Vorbis("Opus", set);
            case "wav":
                return "WAV";
            case "aiff" or "aif":
                return "AIFF"; // Lidarr's title parser has no AIFF codec: reads as Unknown (documented in tests)
            case "ape":
                return "APE";
            case "wv":
                return "WV";
            default:
                return format.Trim().TrimStart('.').ToUpperInvariant();
        }
    }

    private static string Lossless(string codec, List<CandidateFile> files, string folder)
    {
        var withDepth = files.Where(f => f.BitDepth is not null).ToList();
        var hi = withDepth.Count > 0
            ? withDepth.Count(f => f.BitDepth >= 24) * 2 > withDepth.Count
            : Marker24Regex().IsMatch(folder);
        return hi ? codec + " 24bit" : codec;
    }

    private static string Mp3(List<CandidateFile> files, string folder)
    {
        var rated = files.Where(f => f.BitRate is > 0).ToList();
        if (rated.Count > 0)
        {
            if (files.All(f => f.IsVbr == true))
                return rated.Average(f => f.BitRate!.Value) >= 220 ? "MP3 VBR V0" : "MP3 VBR V2";
            return Coded("MP3", Mp3Bitrates, Modal(rated));
        }

        var vbr = MarkerVbrRegex().Match(folder);
        if (vbr.Success)
            return "MP3 VBR V" + vbr.Groups[1].Value;
        var rate = MarkerBitrateRegex().Match(folder);
        return rate.Success ? "MP3 " + rate.Groups[1].Value : "MP3";
    }

    private static string Aac(List<CandidateFile> files, string folder)
    {
        var rated = files.Where(f => f.BitRate is > 0).ToList();
        if (rated.Count > 0)
            return Coded("AAC", AacBitrates, Modal(rated));
        var rate = MarkerBitrateRegex().Match(folder);
        return rate.Success ? "AAC " + rate.Groups[1].Value : "AAC";
    }

    private static string Vorbis(string codec, List<CandidateFile> files)
    {
        var rated = files.Where(f => f.BitRate is > 0).ToList();
        return rated.Count > 0 ? Coded(codec, VorbisBitrates, Modal(rated)) : codec;
    }

    private static int Modal(List<CandidateFile> rated) =>
        rated.GroupBy(f => f.BitRate!.Value).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

    /// <summary>
    /// Never rounds up into a higher bucket (that would overstate quality): picks the highest mapped bucket
    /// at most 5% above the actual bitrate. Below the lowest bucket the codec is emitted without a bitrate.
    /// </summary>
    private static string Coded(string codec, int[] buckets, int bitrate)
    {
        var fit = buckets.Where(b => b <= bitrate * 1.05).DefaultIfEmpty(0).Max();
        return fit == 0 ? codec : $"{codec} {fit}";
    }
}

/// <summary>Builds the Lidarr-facing release title: <c>Artist - Album (Year) [TAG]</c>.</summary>
public static class MusicTitle
{
    public static string Build(ParsedName name, string qualityTag)
    {
        if (string.IsNullOrWhiteSpace(name.Author) || string.IsNullOrWhiteSpace(name.Title))
            return $"{name.Stem} [{qualityTag}]";
        var year = name.Year is { } y ? $" ({y})" : "";
        return $"{name.Author} - {name.Title}{year} [{qualityTag}]";
    }
}
