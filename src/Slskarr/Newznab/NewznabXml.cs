using System.Globalization;
using System.Xml.Linq;
using Slskarr.Data;
using Slskarr.Nzb;

namespace Slskarr.Newznab;

/// <summary>Builds the Newznab XML documents: caps, RSS feed and error.</summary>
internal static class NewznabXml
{
    public static readonly XNamespace Attr = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    public static byte[] Caps() => StubNzb.Serialize(new XDocument(new XDeclaration("1.0", "utf-8", null),
        new XElement("caps",
            new XElement("server", new XAttribute("title", "Slskarr")),
            new XElement("limits", new XAttribute("max", 100), new XAttribute("default", 100)),
            new XElement("searching",
                new XElement("search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q")),
                new XElement("book-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,author,title")),
                new XElement("tv-search", new XAttribute("available", "no")),
                new XElement("movie-search", new XAttribute("available", "no")),
                new XElement("audio-search", new XAttribute("available", "no"))),
            new XElement("categories",
                new XElement("category", new XAttribute("id", 7000), new XAttribute("name", "Books"),
                    new XElement("subcat", new XAttribute("id", 7020), new XAttribute("name", "Books/EBook")))))));

    public static byte[] Error(int code, string description) => StubNzb.Serialize(new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("error", new XAttribute("code", code), new XAttribute("description", description))));

    public static byte[] Feed(IEnumerable<HitEntity> hits, string baseUrl, string? apiKey, int offset)
    {
        var channel = new XElement("channel",
            new XElement("title", "Slskarr"),
            new XElement("description", "Slskarr Soulseek indexer"),
            new XElement("link", baseUrl),
            new XElement(Attr + "response", new XAttribute("offset", offset)));
        foreach (var hit in hits)
        {
            channel.Add(Item(hit, baseUrl, apiKey));
        }

        return StubNzb.Serialize(new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "newznab", Attr.NamespaceName), channel)));
    }

    private static XElement Item(HitEntity hit, string baseUrl, string? apiKey)
    {
        var url = $"{baseUrl}/newznab/api?t=get&id={Uri.EscapeDataString(hit.Id)}"
            + (string.IsNullOrWhiteSpace(apiKey) ? "" : $"&apikey={Uri.EscapeDataString(apiKey)}");
        var size = hit.Size.ToString(CultureInfo.InvariantCulture);
        var item = new XElement("item",
            new XElement("title", hit.ReleaseTitle),
            new XElement("guid", new XAttribute("isPermaLink", "false"), hit.Id),
            new XElement("link", url),
            new XElement("comments", $"{baseUrl}/release/{Uri.EscapeDataString(hit.Id)}"),
            new XElement("pubDate", hit.SeenAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
            new XElement("size", size),
            new XElement("enclosure", new XAttribute("url", url), new XAttribute("length", size), new XAttribute("type", "application/x-nzb")),
            Attribute("category", "7000"),
            Attribute("category", "7020"),
            Attribute("size", size));
        if (!string.IsNullOrWhiteSpace(hit.ParsedAuthor))
        {
            item.Add(Attribute("author", hit.ParsedAuthor));
        }

        if (!string.IsNullOrWhiteSpace(hit.ParsedTitle))
        {
            item.Add(Attribute("booktitle", hit.ParsedTitle));
        }

        return item;
    }

    private static XElement Attribute(string name, string value) =>
        new(Attr + "attr", new XAttribute("name", name), new XAttribute("value", value));
}
