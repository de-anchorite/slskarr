using System.Globalization;
using System.Xml.Linq;
using Slskarr.Data;
using Slskarr.Nzb;
using Slskarr.Profiles;

namespace Slskarr.Newznab;

/// <summary>Builds the Newznab XML documents: caps, RSS feed and error.</summary>
internal static class NewznabXml
{
    public static readonly XNamespace Attr = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    public static byte[] Caps(IReadOnlyList<IMediaProfile> profiles)
    {
        var searching = new XElement("searching",
            new XElement("search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q")),
            new XElement("book-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,author,title")),
            new XElement("tv-search", new XAttribute("available", "no")),
            new XElement("movie-search", new XAttribute("available", "no")),
            profiles.Any(p => p.CapsCategory.Id == 3000)
                ? new XElement("audio-search", new XAttribute("available", "yes"), new XAttribute("supportedParams", "q,artist,album"))
                : new XElement("audio-search", new XAttribute("available", "no")));
        var categories = new XElement("categories", profiles.Select(p => new XElement("category",
            new XAttribute("id", p.CapsCategory.Id), new XAttribute("name", p.CapsCategory.Name),
            p.CapsCategory.Subcats.Select(c => new XElement("subcat", new XAttribute("id", c.Id), new XAttribute("name", c.Name))))));
        return StubNzb.Serialize(new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("caps",
                new XElement("server", new XAttribute("title", "Slskarr")),
                new XElement("limits", new XAttribute("max", 100), new XAttribute("default", 100)),
                searching,
                categories)));
    }

    public static byte[] Error(int code, string description) => StubNzb.Serialize(new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("error", new XAttribute("code", code), new XAttribute("description", description))));

    /// <param name="placeholderAt">When set, the feed holds only <see cref="NewznabPlaceholder"/>, dated this time.</param>
    public static byte[] Feed(
        IEnumerable<HitEntity> hits, ProfileRegistry registry, IMediaProfile? placeholderProfile, string baseUrl, string? apiKey, int offset, DateTimeOffset? placeholderAt = null)
    {
        var channel = new XElement("channel",
            new XElement("title", "Slskarr"),
            new XElement("description", "Slskarr Soulseek indexer"),
            new XElement("link", baseUrl),
            new XElement(Attr + "response", new XAttribute("offset", offset)));
        foreach (var hit in hits)
        {
            channel.Add(Item(hit, registry.ByName(hit.Profile) ?? registry.All[0], baseUrl, apiKey));
        }

        if (placeholderAt is { } at)
        {
            channel.Add(Item(new HitEntity
            {
                Id = NewznabPlaceholder.Id, SearchKey = "", Profile = placeholderProfile?.Name ?? "", Username = "", RemotePath = "",
                ReleaseTitle = NewznabPlaceholder.Title, Size = NewznabPlaceholder.Size, Format = "", SeenAt = at,
            }, placeholderProfile ?? registry.All[0], baseUrl, apiKey));
        }

        return StubNzb.Serialize(new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "newznab", Attr.NamespaceName), channel)));
    }

    private static XElement Item(HitEntity hit, IMediaProfile profile, string baseUrl, string? apiKey)
    {
        var url = $"{baseUrl}/newznab/api?t=get&id={Uri.EscapeDataString(hit.Id)}"
            + (string.IsNullOrWhiteSpace(apiKey) ? "" : $"&apikey={Uri.EscapeDataString(apiKey)}");
        var size = hit.Size.ToString(CultureInfo.InvariantCulture);
        var item = new XElement("item",
            new XElement("title", XmlText.Clean(hit.ReleaseTitle)),
            new XElement("guid", new XAttribute("isPermaLink", "false"), XmlText.Clean(hit.Id)),
            new XElement("link", url),
            new XElement("comments", $"{baseUrl}/release/{Uri.EscapeDataString(hit.Id)}"),
            new XElement("pubDate", hit.SeenAt.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
            new XElement("size", size),
            new XElement("enclosure", new XAttribute("url", url), new XAttribute("length", size), new XAttribute("type", "application/x-nzb")),
            profile.ItemCategories(hit).Select(c => Attribute("category", c.ToString(CultureInfo.InvariantCulture))),
            Attribute("size", size));
        foreach (var (name, value) in profile.ItemAttributes(hit))
        {
            item.Add(Attribute(name, XmlText.Clean(value)));
        }

        return item;
    }

    private static XElement Attribute(string name, string value) =>
        new(Attr + "attr", new XAttribute("name", name), new XAttribute("value", value));
}
