using System.Text.RegularExpressions;
using Slskarr.Soulseek;

namespace Slskarr.Profiles.Ebook;

public sealed partial class EbookNameParser
{
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "books", "book", "ebooks", "ebook", "e-books", "e-book", "epub", "epubs", "mobi", "pdf", "azw3",
        "library", "calibre", "calibre library", "music", "share", "shares", "shared", "downloads",
        "incoming", "complete", "completed", "fiction", "non-fiction", "nonfiction", "literature",
        "audiobooks", "comics", "misc", "other",
    };

    private static readonly HashSet<string> FormatWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "epub", "mobi", "azw", "azw3", "pdf", "djvu", "fb2", "cbz", "cbr", "txt", "rtf", "doc", "docx",
        "lit", "kindle", "ebook", "retail",
    };

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionRegex();

    [GeneratedRegex(@"\s*[\[(]([^\[\]()]*)[\])]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingGroupRegex();

    [GeneratedRegex(@"[\s\-]*\bISBN[\s:_\-]*([\dXx\-]+)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BareIsbnRegex();

    [GeneratedRegex(@"^(?:97[89][\s\-]?)?[\d\-\s]{9,14}[\dXx]?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsbnLikeRegex();

    [GeneratedRegex(@"^(?:1[5-9]|20)\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s+-\s+((?:1[5-9]|20)\d\d)$", RegexOptions.CultureInvariant)]
    private static partial Regex DashYearRegex();

    [GeneratedRegex(@"^v\d+(?:\.\d+)*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?<=\w)\.(?=\w)", RegexOptions.CultureInvariant)]
    private static partial Regex WordDotRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(\d{1,3})\s*(?:-|\.)\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SeriesIndexRegex();

    [GeneratedRegex(@"^@@\w+$", RegexOptions.CultureInvariant)]
    private static partial Regex ShareRootRegex();

    [GeneratedRegex(@"\s*\(\d+\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CalibreIdRegex();

    [GeneratedRegex(@"\s+by\s+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
    private static partial Regex ByRegex();

    public ParsedName Parse(string remotePath)
    {
        var segments = SoulseekPath.Segments(remotePath);
        var fileName = segments.Count == 0 ? string.Empty : segments[^1];
        var format = SoulseekPath.Extension(remotePath).ToUpperInvariant();
        // Parent folders, nearest first.
        var folders = segments.Take(Math.Max(segments.Count - 1, 0)).Reverse().ToList();

        var (stem, year) = Clean(fileName);
        var (author, title) = Split(stem, folders);
        return new ParsedName(author, title, year, format, stem);
    }

    private static (string? Author, string Title) Split(string stem, List<string> folders)
    {
        // Series index: "05 - Sourcery" / "05. Sourcery". Checked first so "05" is never an author.
        var series = SeriesIndexRegex().Match(stem);
        if (series.Success)
            return (SeriesAuthor(folders), series.Groups[2].Value.Trim());

        // Calibre: "Title - Author" when the grandparent folder is that author.
        var lastDash = stem.LastIndexOf(" - ", StringComparison.Ordinal);
        if (lastDash > 0 && folders.Count >= 2)
        {
            var title = stem[..lastDash].Trim();
            var author = stem[(lastDash + 3)..].Trim();
            if (title.Length > 0 && author.Length > 0 && string.Equals(Clean(folders[1]).Text, author, StringComparison.OrdinalIgnoreCase))
                return (author, title);
        }

        // Author - Title
        var firstDash = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (firstDash > 0)
        {
            var author = stem[..firstDash].Trim();
            var title = stem[(firstDash + 3)..].Trim();
            if (author.Length > 0 && title.Length > 0)
                return (author, title);
        }

        // Title by Author
        var by = ByRegex().Match(stem);
        if (by.Success && by.Index > 0)
        {
            var title = stem[..by.Index].Trim();
            var author = stem[(by.Index + by.Length)..].Trim();
            if (title.Length > 0 && author.Length > 0)
                return (author, title);
        }

        // Bare title: the parent folder is the author when it is not generic.
        if (folders.Count > 0 && !IsGeneric(folders[0]))
        {
            var parent = Clean(folders[0]).Text;
            if (CalibreIdRegex().IsMatch(parent))
            {
                // Calibre "Title (id)" folder: the author is one level up.
                if (folders.Count > 1 && !IsGeneric(folders[1]))
                    return (Clean(folders[1]).Text, stem);
            }
            else if (parent.Length > 0)
            {
                return (parent, stem);
            }
        }

        return (null, stem);
    }

    private static string? SeriesAuthor(List<string> folders)
    {
        var nearest = folders.FindIndex(f => !IsGeneric(f));
        if (nearest < 0)
            return null;
        var further = folders.FindIndex(nearest + 1, f => !IsGeneric(f));
        return Clean(folders[further >= 0 ? further : nearest]).Text;
    }

    private static bool IsGeneric(string folder)
    {
        var name = folder.Trim();
        return name.Length == 0 || GenericFolders.Contains(name) || ShareRootRegex().IsMatch(name);
    }

    /// <summary>Applies parsing rules 1 and 2 to a file or folder name.</summary>
    private static (string Text, int? Year) Clean(string name)
    {
        var text = name;
        var ext = ExtensionRegex().Match(text);
        if (ext.Success && ext.Index > 0)
            text = text[..ext.Index];

        // Dots between words become spaces only for dotted names ("Dune.by.Frank.Herbert").
        if (!text.Contains(' ') && !text.Contains('_'))
            text = WordDotRegex().Replace(text, " ");
        text = text.Replace('_', ' ');
        text = WhitespaceRegex().Replace(text, " ").Trim();

        int? year = null;
        bool changed;
        do
        {
            changed = false;

            var isbn = BareIsbnRegex().Match(text);
            if (isbn.Success && IsIsbn(isbn.Groups[1].Value))
            {
                text = text[..isbn.Index].TrimEnd();
                changed = true;
                continue;
            }

            var group = TrailingGroupRegex().Match(text);
            if (group.Success && group.Index > 0)
            {
                var content = group.Groups[1].Value.Trim();
                if (YearRegex().IsMatch(content))
                {
                    year ??= int.Parse(content);
                    text = text[..group.Index].TrimEnd();
                    changed = true;
                }
                else if (IsNoiseGroup(content))
                {
                    text = text[..group.Index].TrimEnd();
                    changed = true;
                }
            }
        } while (changed);

        // " - 1999" counts as a year only when something else precedes another " - " (so "Orwell - 1984" keeps its title).
        var dashYear = DashYearRegex().Match(text);
        if (dashYear.Success && text[..dashYear.Index].Contains(" - ", StringComparison.Ordinal))
        {
            year ??= int.Parse(dashYear.Groups[1].Value);
            text = text[..dashYear.Index].TrimEnd();
        }

        text = text.TrimEnd(' ', '-').TrimStart();
        return (text, year);
    }

    private static bool IsNoiseGroup(string content)
    {
        if (content.Length == 0)
            return false;
        var tokens = content.Split([',', ' ', '/', ';', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (FormatWords.Contains(token) || VersionRegex().IsMatch(token))
                continue;
            if (token.StartsWith("ISBN", StringComparison.OrdinalIgnoreCase))
            {
                var rest = token.TrimStart("ISBNisbn".ToCharArray()).TrimStart(':', '-');
                if (rest.Length == 0 && i + 1 < tokens.Length)
                    continue; // "ISBN 0441013597": the number is the next token
                if (IsIsbn(rest))
                    continue;
                return false;
            }
            if (IsIsbn(token))
                continue;
            return false;
        }

        return true;
    }

    private static bool IsIsbn(string value)
    {
        var compact = value.Replace("-", "").Replace(" ", "");
        if (compact.Length is not (10 or 13) || !IsbnLikeRegex().IsMatch(value))
            return false;
        for (var i = 0; i < compact.Length; i++)
        {
            var c = compact[i];
            if (!char.IsAsciiDigit(c) && !(i == compact.Length - 1 && c is 'X' or 'x'))
                return false;
        }

        return true;
    }
}
