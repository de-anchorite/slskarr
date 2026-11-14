using System.Xml;
using System.Xml.Linq;
using Shouldly;
using Slskarr.Data;
using Slskarr.Nzb;

namespace Slskarr.Tests.Nzb;

public sealed class StubNzbTests
{
    private static readonly XNamespace NzbNs = "http://www.newzbin.com/DTD/2003/nzb";

    internal static HitEntity Hit(string id = "AAAAAAAAAAAAAAAAAAAAAA", string title = "Frank Herbert - Dune (1965)") => new()
    {
        Id = id,
        SearchKey = "ebook:dune",
        Profile = "ebook",
        Username = "peer",
        RemotePath = @"@@abcde\Books\Frank Herbert\Frank Herbert - Dune (1965).epub",
        Size = 1_234_567,
        ReleaseTitle = title,
        Format = "EPUB",
        SeenAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Build_passes_arr_nzb_validation()
    {
        var bytes = StubNzb.Build(Hit());

        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var doc = XDocument.Load(reader);

        doc.Root!.Name.LocalName.ShouldBe("nzb");
        doc.Root.Name.Namespace.ShouldBe(NzbNs);
        doc.Root.Elements(NzbNs + "file").Count().ShouldBeGreaterThanOrEqualTo(1);
        doc.Root.Name.LocalName.ShouldNotBe("error");
        doc.Root.Element(NzbNs + "file")!.Element(NzbNs + "segments")!.Element(NzbNs + "segment")!
            .Attribute("bytes")!.Value.ShouldBe("1234567");
    }

    [Fact]
    public void ReadHitId_roundtrip()
    {
        var hit = Hit("AbC-_123456789012345Z", "Title with \"quotes\" & <angles>");

        StubNzb.ReadHitId(new MemoryStream(StubNzb.Build(hit))).ShouldBe(hit.Id);
    }

    [Fact]
    public void ReadHitId_returns_null_for_foreign_nzb()
    {
        const string foreign = """
            <?xml version="1.0"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb"><head><meta type="name">x</meta></head>
            <file poster="a" date="1" subject="s"><groups><group>g</group></groups><segments><segment bytes="1" number="1">x@y</segment></segments></file></nzb>
            """;

        StubNzb.ReadHitId(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(foreign))).ShouldBeNull();
        StubNzb.ReadHitId(new MemoryStream("not xml"u8.ToArray())).ShouldBeNull();
    }

    [Fact]
    public void Invalid_xml_chars_in_names_are_sanitised()
    {
        var hit = Hit("AAAAAAAAAAAAAAAAAAAAAA", "Bad\u0001Title \ud800 \ud83d\ude00 \u4e2d");
        hit.RemotePath = "@@a\\dir\\bad\u0002name.epub";

        var bytes = StubNzb.Build(hit);

        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var doc = XDocument.Load(reader);
        doc.Root!.Elements(NzbNs + "file").Count().ShouldBe(1);
        doc.Descendants(NzbNs + "meta").Single(m => (string?)m.Attribute("type") == "name").Value.ShouldContain("\ud83d\ude00");
        StubNzb.ReadHitId(new MemoryStream(bytes)).ShouldBe(hit.Id);
    }
}
