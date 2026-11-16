using System.Text.RegularExpressions;
using Slskarr.Soulseek;

namespace Slskarr.Profiles.Music;

/// <summary>Parses a Soulseek album folder path into artist, album and year. Pure; names come only from the path.</summary>
public static partial class MusicNameParser
{
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "mp3", "mp3s", "flac", "lossless", "albums", "album", "share", "shares", "shared", "downloads",
        "incoming", "complete", "completed", "library", "audio", "misc", "other", "various", "soulseek",
    };

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "mp3", "aac", "m4a", "alac", "ogg", "opus", "wav", "ape", "wv", "aiff", "web", "cd", "vinyl",
        "lossless", "320", "256", "192", "v0", "v2", "24bit", "24-bit", "16bit", "cbr", "vbr", "kbps", "320kbps",
    };

    [GeneratedRegex(@"\s*[\[(]([^\[\]()]*)[\])]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingGroupRegex();

    [GeneratedRegex(@"^\s*[\[(]((?:19|20)\d\d)[\])]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingYearGroupRegex();

    [GeneratedRegex(@"^(?:19|20)\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?<=\w)\.(?=\w)", RegexOptions.CultureInvariant)]
    private static partial Regex WordDotRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^@@\w+$|^[A-Za-z]:$", RegexOptions.CultureInvariant)]
    private static partial Regex RootRegex();

    public static ParsedName Parse(string releasePath)
    {
        var segments = SoulseekPath.Segments(releasePath);
        if (segments.Count == 0)
            return new ParsedName(null, null, null, "", string.Empty);

        var (stem, year) = Clean(segments[^1]);
        var parts = stem.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        // Year-only parts ("Artist - 1997 - Album", "1997 - Album") carry the year, not the name.
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (parts.Count > 1 && YearRegex().IsMatch(parts[i]))
            {
                year ??= int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
                parts.RemoveAt(i);
            }
        }

        if (parts.Count >= 2)
            return new ParsedName(parts[0], string.Join(" - ", parts.Skip(1)), year, "", stem);

        // Single name: the parent folder is the artist unless it is generic.
        if (parts.Count == 1 && segments.Count >= 2 && !IsGeneric(segments[^2]))
        {
            var artist = Clean(segments[^2]).Text;
            if (artist.Length > 0)
                return new ParsedName(artist, parts[0], year, "", stem);
        }

        return new ParsedName(null, null, year, "", stem);
    }

    private static bool IsGeneric(string folder)
    {
        var name = folder.Trim();
        return name.Length == 0 || GenericFolders.Contains(name) || RootRegex().IsMatch(name);
    }

    private static (string Text, int? Year) Clean(string name)
    {
        var text = name;
        if (!text.Contains(' ') && !text.Contains('_'))
            text = WordDotRegex().Replace(text, " ");
        text = text.Replace('_', ' ');
        text = WhitespaceRegex().Replace(text, " ").Trim();

        int? year = null;
        var leading = LeadingYearGroupRegex().Match(text);
        if (leading.Success)
        {
            year = int.Parse(leading.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            text = text[leading.Length..];
        }

        bool changed;
        do
        {
            changed = false;
            var group = TrailingGroupRegex().Match(text);
            if (!group.Success || group.Index <= 0)
                continue;
            var content = group.Groups[1].Value.Trim();
            if (YearRegex().IsMatch(content))
            {
                year ??= int.Parse(content, System.Globalization.CultureInfo.InvariantCulture);
                text = text[..group.Index].TrimEnd();
                changed = true;
            }
            else if (IsNoiseGroup(content))
            {
                text = text[..group.Index].TrimEnd();
                changed = true;
            }
        } while (changed);

        return (text.Trim().TrimEnd('-').TrimEnd(), year);
    }

    private static bool IsNoiseGroup(string content)
    {
        var tokens = content.Split([',', ' ', '/', ';', '+'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens.All(t => NoiseWords.Contains(t));
    }
}
