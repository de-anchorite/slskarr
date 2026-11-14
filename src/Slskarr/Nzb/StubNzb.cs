using System.Text;
using System.Xml;
using System.Xml.Linq;
using Slskarr.Data;

namespace Slskarr.Nzb;

/// <summary>
/// The "NZB" handed to the *arrs. It carries no usenet data, only the hit id; it is shaped to pass
/// the *arr NZB validation (root <c>nzb</c>, at least one <c>file</c>).
/// </summary>
public static class StubNzb
{
    public static readonly XNamespace Ns = "http://www.newzbin.com/DTD/2003/nzb";
    private const string IdMeta = "slskarr-id";

    public static byte[] Build(HitEntity hit)
    {
        var fileName = XmlText.Clean(hit.RemotePath[(hit.RemotePath.LastIndexOfAny(['\\', '/']) + 1)..]);
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "nzb",
                new XElement(Ns + "head",
                    new XElement(Ns + "meta", new XAttribute("type", IdMeta), XmlText.Clean(hit.Id)),
                    new XElement(Ns + "meta", new XAttribute("type", "name"), XmlText.Clean(hit.ReleaseTitle))),
                new XElement(Ns + "file",
                    new XAttribute("poster", "slskarr"),
                    new XAttribute("date", hit.SeenAt.ToUnixTimeSeconds()),
                    new XAttribute("subject", $"slskarr:{XmlText.Clean(hit.Id)} \"{fileName}\""),
                    new XElement(Ns + "groups", new XElement(Ns + "group", "alt.binaries.slskarr")),
                    new XElement(Ns + "segments",
                        new XElement(Ns + "segment", new XAttribute("bytes", hit.Size), new XAttribute("number", 1), $"{XmlText.Clean(hit.Id)}@slskarr")))));
        return Serialize(doc);
    }

    /// <summary>The hit id inside a Slskarr stub NZB, or null for any other (or unparseable) document.</summary>
    public static string? ReadHitId(Stream nzb)
    {
        try
        {
            using var reader = XmlReader.Create(nzb, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            var doc = XDocument.Load(reader);
            return doc.Root?.Element(Ns + "head")?.Elements(Ns + "meta")
                .FirstOrDefault(m => (string?)m.Attribute("type") == IdMeta)?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    internal static byte[] Serialize(XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            doc.Save(w);
        }

        return ms.ToArray();
    }
}
