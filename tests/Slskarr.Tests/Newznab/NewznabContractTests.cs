using System.Globalization;
using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Slskarr.Data;
using Slskarr.Newznab;
using Slskarr.Nzb;
using Slskarr.Profiles;
using Slskarr.Search;

namespace Slskarr.Tests.Newznab;

public sealed class NewznabContractTests : IDisposable
{
    private static readonly XNamespace Nn = "http://www.newznab.com/DTD/2010/feeds/attributes/";
    private static readonly SearchRequest DuneRequest = new(null, "Wrong", "Wrong", [7020]);

    private readonly SlskarrFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task Seed(SearchRequest? doneFor, params HitEntity[] hits)
    {
        var dbf = _factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        var now = _factory.Time.GetUtcNow();
        if (doneFor is not null)
        {
            var profile = _factory.Services.GetRequiredService<ProfileRegistry>().All[0];
            var key = SearchKey.For(profile, doneFor).Value;
            db.SearchJobs.Add(new SearchJobEntity
            {
                Key = key, Profile = "ebook", QueryText = profile.BuildQuery(doneFor), RequestJson = SearchCoordinator.WriteRequest(doneFor),
                State = SearchJobState.Done, CreatedAt = now, LastRequestedAt = now, CompletedAt = now,
            });
            foreach (var h in hits)
            {
                h.SearchKey = key;
            }
        }

        db.Hits.AddRange(hits);
        await db.SaveChangesAsync();
    }

    private static HitEntity Hit(string id, string title, int rank = 1, string? author = "Frank Herbert", string? book = "Dune", int ageMinutes = 0) =>
        new()
        {
            Id = id, SearchKey = "x", Profile = "ebook", Username = "peer", RemotePath = @"@@a\Books\" + title + ".epub", Size = 1_000_000,
            ReleaseTitle = title, ParsedAuthor = author, ParsedTitle = book, Format = "EPUB", Rank = rank,
            SeenAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) - TimeSpan.FromMinutes(ageMinutes),
        };

    private static string?[] Values(XElement i, string name) =>
        [.. i.Elements(Nn + "attr").Where(a => a.Attribute("name")!.Value == name).Select(a => a.Attribute("value")!.Value)];

    private static async Task<XDocument> Xml(HttpResponseMessage r) => XDocument.Parse(await r.Content.ReadAsStringAsync());

    [Fact]
    public async Task Caps_parse_like_chaptarr()
    {
        using var client = _factory.CreateClient();
        var r = await client.GetAsync("/newznab/api?t=caps");

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
        var caps = (await Xml(r)).Root!;
        caps.Name.LocalName.ShouldBe("caps");
        var limits = caps.Element("limits")!;
        int.Parse(limits.Attribute("max")!.Value, CultureInfo.InvariantCulture).ShouldBe(100);
        int.Parse(limits.Attribute("default")!.Value, CultureInfo.InvariantCulture).ShouldBe(100);
        caps.Element("server")!.Attribute("title")!.Value.ShouldBe("Slskarr");
        var searching = caps.Element("searching")!;
        searching.Element("book-search")!.Attribute("available")!.Value.ShouldBe("yes");
        var p = searching.Element("book-search")!.Attribute("supportedParams")!.Value.Split(',');
        p.ShouldContain("author");
        p.ShouldContain("title");
        searching.Element("search")!.Attribute("available")!.Value.ShouldBe("yes");
        searching.Element("tv-search")!.Attribute("available")!.Value.ShouldBe("no");
        searching.Element("movie-search")!.Attribute("available")!.Value.ShouldBe("no");
        searching.Element("audio-search")!.Attribute("available")!.Value.ShouldBe("yes");
        caps.Descendants("category").Concat(caps.Descendants("subcat")).Select(c => c.Attribute("id")!.Value).ShouldContain("7020");
    }

    private static HitEntity MusicHit(string id, string title, int ageMinutes = 0, string format = "FLAC") =>
        new()
        {
            Id = id, SearchKey = "x", Profile = "music", Username = "peer", RemotePath = @"@@a\Music\Radiohead\OK Computer", Size = 300_000_000,
            ReleaseTitle = title, ParsedAuthor = "Radiohead", ParsedTitle = "OK Computer", Format = format, Rank = 1,
            SeenAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) - TimeSpan.FromMinutes(ageMinutes),
        };

    private async Task SeedMusic(SearchRequest? doneFor, params HitEntity[] hits)
    {
        var dbf = _factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        var now = _factory.Time.GetUtcNow();
        if (doneFor is not null)
        {
            var profile = _factory.Services.GetRequiredService<ProfileRegistry>().ByName("music")!;
            var key = SearchKey.For(profile, doneFor).Value;
            db.SearchJobs.Add(new SearchJobEntity
            {
                Key = key, Profile = "music", QueryText = profile.BuildQuery(doneFor), RequestJson = SearchCoordinator.WriteRequest(doneFor),
                State = SearchJobState.Done, CreatedAt = now, LastRequestedAt = now, CompletedAt = now,
            });
            foreach (var h in hits)
            {
                h.SearchKey = key;
            }
        }

        db.Hits.AddRange(hits);
        await db.SaveChangesAsync();
    }

    private static readonly SearchRequest OkComputer = new(null, null, null, [3000, 3010, 3040], "Radiohead", "OK Computer", "music");

    [Fact]
    public async Task Caps_announce_audio_search_and_categories()
    {
        using var client = _factory.CreateClient();

        var caps = (await Xml(await client.GetAsync("/newznab/api?t=caps"))).Root!;

        var audio = caps.Element("searching")!.Element("audio-search")!;
        audio.Attribute("available")!.Value.ShouldBe("yes");
        var supported = audio.Attribute("supportedParams")!.Value.Split(',');
        supported.ShouldContain("q");
        supported.ShouldContain("artist");
        supported.ShouldContain("album");
        var categories = caps.Element("categories")!.Elements("category").ToDictionary(c => c.Attribute("id")!.Value);
        categories.Keys.ShouldBe(["7000", "3000"], ignoreOrder: true);
        categories["3000"].Elements("subcat").Select(s => s.Attribute("id")!.Value).ShouldBe(["3010", "3040"], ignoreOrder: true);
        categories["7000"].Elements("subcat").Select(s => s.Attribute("id")!.Value).ShouldBe(["7020"]);
    }

    [Fact]
    public async Task Music_search_returns_music_items()
    {
        await SeedMusic(OkComputer, MusicHit("MMMMMMMMMMMMMMMMMMMMMM", "Radiohead - OK Computer (1997) [FLAC]"));
        using var client = _factory.CreateClient();

        var doc = await Xml(await client.GetAsync(
            "/newznab/api?t=music&cat=3000,3010,3040&extended=1&offset=0&limit=100&q=&artist=Radiohead&album=OK%20Computer"));

        var item = doc.Descendants("item").ShouldHaveSingleItem();
        item.Element("title")!.Value.ShouldBe("Radiohead - OK Computer (1997) [FLAC]");
        Values(item, "category").ShouldBe(["3000", "3040"]);
        Values(item, "artist").ShouldBe(["Radiohead"]);
        Values(item, "album").ShouldBe(["OK Computer"]);
        Values(item, "author").ShouldBeEmpty();
    }

    [Fact]
    public async Task Lidarr_tier2_is_one_search()
    {
        var slskd = new Slskarr.Tests.Search.FakeSearchSlskd(TimeProvider.System);
        using var factory = new SlskarrFactory().WithServices(s => s.AddSingleton<Slskarr.Soulseek.ISlskdClient>(slskd));
        var dbf = factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        var profile = factory.Services.GetRequiredService<ProfileRegistry>().ByName("music")!;
        await using (var db = await dbf.CreateDbContextAsync())
        {
            var now = factory.Time.GetUtcNow();
            db.SearchJobs.Add(new SearchJobEntity
            {
                Key = SearchKey.For(profile, OkComputer).Value, Profile = "music", QueryText = "Radiohead OK Computer",
                RequestJson = SearchCoordinator.WriteRequest(OkComputer), State = SearchJobState.Done,
                CreatedAt = now, LastRequestedAt = now, CompletedAt = now,
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var r = await client.GetAsync("/newznab/api?t=search&cat=3000,3010,3040&extended=1&offset=0&limit=100&q=Radiohead%20OK%20Computer");

        (await Xml(r)).Descendants("item").ShouldBeEmpty();
        slskd.StartedTexts.ShouldBeEmpty();
        await using var check = await dbf.CreateDbContextAsync();
        check.SearchJobs.Count().ShouldBe(1);
    }

    // Lidarr's self-titled shape: tier 1 sends year separately, tier 2 appends it to "artist album".
    [Fact]
    public async Task Lidarr_self_titled_tiers_are_one_search()
    {
        var slskd = new Slskarr.Tests.Search.FakeSearchSlskd(TimeProvider.System);
        using var factory = new SlskarrFactory().WithServices(s => s.AddSingleton<Slskarr.Soulseek.ISlskdClient>(slskd));
        var dbf = factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        var profile = factory.Services.GetRequiredService<ProfileRegistry>().ByName("music")!;
        var weezer = new SearchRequest(null, null, null, [3000, 3010, 3040], "Weezer", "Weezer", "music");
        SearchKey.For(profile, weezer).Value.ShouldBe("music:weezer");
        await using (var db = await dbf.CreateDbContextAsync())
        {
            var now = factory.Time.GetUtcNow();
            db.SearchJobs.Add(new SearchJobEntity
            {
                Key = SearchKey.For(profile, weezer).Value, Profile = "music", QueryText = profile.BuildQuery(weezer),
                RequestJson = SearchCoordinator.WriteRequest(weezer), State = SearchJobState.Done,
                CreatedAt = now, LastRequestedAt = now, CompletedAt = now,
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        foreach (var url in new[]
                 {
                     "/newznab/api?t=music&cat=3000,3010,3040&extended=1&offset=0&limit=100&q=&artist=Weezer&album=Weezer&year=1994",
                     "/newznab/api?t=search&cat=3000,3010,3040&extended=1&offset=0&limit=100&q=Weezer%20Weezer%201994",
                 })
        {
            (await Xml(await client.GetAsync(url))).Descendants("item").ShouldBeEmpty(url);
        }

        slskd.StartedTexts.ShouldBeEmpty();
        await using var check = await dbf.CreateDbContextAsync();
        check.SearchJobs.Select(j => j.Key).ShouldBe(["music:weezer"]);
        check.SearchJobs.Single().QueryText.ShouldBe("Weezer");
    }

    [Fact]
    public async Task Music_rss_never_searches()
    {
        var slskd = new Slskarr.Tests.Search.FakeSearchSlskd(TimeProvider.System);
        using var factory = new SlskarrFactory().WithServices(s => s.AddSingleton<Slskarr.Soulseek.ISlskdClient>(slskd));
        using var client = factory.CreateClient();

        var item = (await Xml(await client.GetAsync("/newznab/api?t=music&cat=3000"))).Descendants("item").ShouldHaveSingleItem();

        item.Element("guid")!.Value.ShouldBe(NewznabPlaceholder.Id);
        Values(item, "category").ShouldContain("3000");
        Values(item, "category").ShouldNotContain("7020");
        slskd.StartedTexts.ShouldBeEmpty();
        var dbf = factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        db.SearchJobs.Count().ShouldBe(0);
    }

    [Fact]
    public async Task Rss_without_cat_lists_both_profiles()
    {
        await Seed(null, Hit("AAAAAAAAAAAAAAAAAAAAAA", "Older book", ageMinutes: 20));
        await SeedMusic(null, MusicHit("MMMMMMMMMMMMMMMMMMMMMM", "Radiohead - OK Computer (1997) [FLAC]", ageMinutes: 5));
        using var client = _factory.CreateClient();

        var doc = await Xml(await client.GetAsync("/newznab/api?t=search"));

        doc.Descendants("item").Select(i => i.Element("title")!.Value)
            .ShouldBe(["Radiohead - OK Computer (1997) [FLAC]", "Older book"]);

        var music = await Xml(await client.GetAsync("/newznab/api?t=music&cat=3000"));
        music.Descendants("item").Select(i => i.Element("title")!.Value).ShouldBe(["Radiohead - OK Computer (1997) [FLAC]"]);
    }

    [Fact]
    public async Task Book_search_items_have_required_fields()
    {
        await Seed(DuneRequest, Hit("AAAAAAAAAAAAAAAAAAAAAA", "Frank Herbert - Dune (1965)"));
        using var client = _factory.CreateClient();

        var r = await client.GetAsync("/newznab/api?t=book&author=Wrong&title=Wrong&cat=7020");

        r.Content.Headers.ContentType!.MediaType.ShouldBe("application/rss+xml");
        var doc = await Xml(r);
        doc.Root!.Attribute("version")!.Value.ShouldBe("2.0");
        var item = doc.Descendants("item").Single();
        item.Element("title")!.Value.ShouldNotBeNullOrWhiteSpace();
        var guid = item.Element("guid")!;
        guid.Value.ShouldBe("AAAAAAAAAAAAAAAAAAAAAA");
        guid.Attribute("isPermaLink")!.Value.ShouldBe("false");
        DateTimeOffset.TryParse(item.Element("pubDate")!.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _).ShouldBeTrue();
        item.Element("pubDate")!.Value.ShouldBe("Thu, 01 Jan 2026 00:00:00 GMT");
        var enc = item.Element("enclosure")!;
        enc.Attribute("type")!.Value.ShouldBe("application/x-nzb");
        enc.Attribute("length")!.Value.ShouldBe("1000000");
        enc.Attribute("url")!.Value.ShouldBe("http://localhost/newznab/api?t=get&id=AAAAAAAAAAAAAAAAAAAAAA");
        item.Element("link")!.Value.ShouldBe(enc.Attribute("url")!.Value);
        item.Element("comments")!.Value.ShouldBe("http://localhost/release/AAAAAAAAAAAAAAAAAAAAAA");
        item.Element("size")!.Value.ShouldBe("1000000");
        var attrs = item.Elements(Nn + "attr").ToDictionary(a => a.Attribute("name")!.Value + "=" + a.Attribute("value")!.Value);
        attrs.ShouldContainKey("category=7020");
        attrs.ShouldContainKey("category=7000");
        attrs.ShouldContainKey("size=1000000");
    }

    [Fact]
    public async Task Author_and_booktitle_attrs_only_from_parsed_name()
    {
        await Seed(DuneRequest,
            Hit("AAAAAAAAAAAAAAAAAAAAAA", "Frank Herbert - Dune (1965)"),
            Hit("BBBBBBBBBBBBBBBBBBBBBB", "unparsable", rank: 2, author: null, book: null));
        using var client = _factory.CreateClient();

        var doc = await Xml(await client.GetAsync("/newznab/api?t=book&author=Wrong&title=Wrong"));

        var items = doc.Descendants("item").ToList();
        items.Count.ShouldBe(2);
        Values(items[0], "author").ShouldBe(["Frank Herbert"]);
        Values(items[0], "booktitle").ShouldBe(["Dune"]);
        Values(items[1], "author").ShouldBeEmpty();
        Values(items[1], "booktitle").ShouldBeEmpty();
    }

    [Fact]
    public async Task Rss_request_shape_from_chaptarr()
    {
        await Seed(null,
            Hit("AAAAAAAAAAAAAAAAAAAAAA", "Older", ageMinutes: 30),
            Hit("BBBBBBBBBBBBBBBBBBBBBB", "Newest", ageMinutes: 0),
            Hit("CCCCCCCCCCCCCCCCCCCCCC", "Middle", ageMinutes: 10));
        using var client = _factory.CreateClient();

        var doc = await Xml(await client.GetAsync("/newznab/api?t=book&cat=7000,7020&extended=1&offset=0&limit=100"));

        doc.Descendants("item").Select(i => i.Element("title")!.Value).ShouldBe(["Newest", "Middle", "Older"]);

        var paged = await Xml(await client.GetAsync("/newznab/api?t=book&cat=7000,7020&offset=1&limit=1"));
        paged.Descendants("item").Select(i => i.Element("title")!.Value).ShouldBe(["Middle"]);
    }

    // Chaptarr/Readarr's connection test fetches the RSS ("recent") feed and Prowlarr's sends t=search with no
    // query; both fail with "Query successful, but no results ..." when the feed has no items.
    [Theory]
    [InlineData("/newznab/api?t=book&cat=7000,7020&extended=1&offset=0&limit=100")]
    [InlineData("/newznab/api?t=search&extended=1&cat=7000,7020")]
    [InlineData("/newznab/api?t=search")]
    public async Task Empty_rss_returns_one_placeholder_so_connection_tests_pass(string url)
    {
        using var client = _factory.CreateClient();

        var r = await client.GetAsync(url);

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = (await Xml(r)).Descendants("item").ShouldHaveSingleItem();
        item.Element("guid")!.Value.ShouldBe(NewznabPlaceholder.Id);
        item.Element("title")!.Value.ShouldNotContain(" - "); // must not parse as "Author - Title"
        DateTimeOffset.TryParse(item.Element("pubDate")!.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _).ShouldBeTrue();
        long.Parse(item.Element("size")!.Value, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        item.Element("enclosure")!.Attribute("type")!.Value.ShouldBe("application/x-nzb");
        Values(item, "category").ShouldContain("7020");
        Values(item, "author").ShouldBeEmpty();
        Values(item, "booktitle").ShouldBeEmpty();
    }

    [Fact]
    public async Task Placeholder_is_not_shown_when_real_hits_exist()
    {
        await Seed(null, Hit("AAAAAAAAAAAAAAAAAAAAAA", "Frank Herbert - Dune [EPUB]"));
        using var client = _factory.CreateClient();

        var items = (await Xml(await client.GetAsync("/newznab/api?t=book&cat=7000,7020&offset=0&limit=100"))).Descendants("item").ToList();

        items.ShouldHaveSingleItem().Element("guid")!.Value.ShouldBe("AAAAAAAAAAAAAAAAAAAAAA");
    }

    [Fact]
    public async Task Placeholder_is_not_shown_for_searches_later_pages_or_other_categories()
    {
        await Seed(DuneRequest); // cached search with no hits
        using var client = _factory.CreateClient();

        foreach (var url in new[]
                 {
                     "/newznab/api?t=book&author=Wrong&title=Wrong&cat=7020",
                     "/newznab/api?t=book&cat=7000,7020&offset=100&limit=100",
                     "/newznab/api?t=search&cat=2000",
                 })
        {
            (await Xml(await client.GetAsync(url))).Descendants("item").ShouldBeEmpty(url);
        }
    }

    [Fact]
    public async Task Get_placeholder_returns_error_300()
    {
        using var client = _factory.CreateClient();

        var e = (await Xml(await client.GetAsync($"/newznab/api?t=get&id={NewznabPlaceholder.Id}"))).Root!;

        e.Name.LocalName.ShouldBe("error");
        e.Attribute("code")!.Value.ShouldBe("300");
    }

    [Fact]
    public async Task Unhandled_category_returns_empty_feed()
    {
        await Seed(null, Hit("AAAAAAAAAAAAAAAAAAAAAA", "Newest"));
        using var client = _factory.CreateClient();

        var r = await client.GetAsync("/newznab/api?t=search&cat=2000");

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Xml(r)).Descendants("item").ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_apikey_returns_error_100()
    {
        using var factory = new SlskarrFactory().WithConfig(c => c["Slskarr:ApiKey"] = "secret");
        using var client = factory.CreateClient();

        foreach (var url in new[] { "/newznab/api?t=caps", "/newznab/api?t=book", "/newznab/api?t=book&apikey=wrong", "/newznab/api?t=get&id=x" })
        {
            var r = await client.GetAsync(url);
            r.StatusCode.ShouldBe(HttpStatusCode.OK);
            r.Content.Headers.ContentType!.MediaType.ShouldBe("application/rss+xml");
            var e = (await Xml(r)).Root!;
            e.Name.LocalName.ShouldBe("error");
            e.Attribute("code")!.Value.ShouldBe("100");
            e.Attribute("description")!.Value.ShouldBe("Incorrect user credentials");
        }

        (await Xml(await client.GetAsync("/newznab/api?t=caps&apikey=secret"))).Root!.Name.LocalName.ShouldBe("caps");
    }

    [Fact]
    public async Task Unknown_function_returns_error_202()
    {
        using var client = _factory.CreateClient();

        var e = (await Xml(await client.GetAsync("/newznab/api?t=tvsearch"))).Root!;

        e.Name.LocalName.ShouldBe("error");
        e.Attribute("code")!.Value.ShouldBe("202");
        e.Attribute("description")!.Value.ShouldBe("No such function");
    }

    [Fact]
    public async Task Get_returns_valid_stub_nzb()
    {
        await Seed(null, Hit("AAAAAAAAAAAAAAAAAAAAAA", "Frank \"Dune\" Herbert — Émile"));
        using var client = _factory.CreateClient();

        var r = await client.GetAsync("/newznab/api?t=get&id=AAAAAAAAAAAAAAAAAAAAAA");

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-nzb");
        r.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        r.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("Frank \"Dune\" Herbert — Émile.nzb");
        StubNzb.ReadHitId(await r.Content.ReadAsStreamAsync()).ShouldBe("AAAAAAAAAAAAAAAAAAAAAA");
    }

    [Fact]
    public async Task Get_unknown_id_returns_error_300()
    {
        using var client = _factory.CreateClient();

        var e = (await Xml(await client.GetAsync("/newznab/api?t=get&id=nope"))).Root!;

        e.Name.LocalName.ShouldBe("error");
        e.Attribute("code")!.Value.ShouldBe("300");
        e.Attribute("description")!.Value.ShouldBe("No such item");
    }

    [Fact]
    public async Task Links_respect_forwarded_host()
    {
        using var factory = new SlskarrFactory().WithConfig(c => c["Slskarr:ApiKey"] = "secret");
        var dbf = factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.Hits.Add(Hit("AAAAAAAAAAAAAAAAAAAAAA", "Newest"));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/newznab/api?t=book&apikey=secret");
        req.Headers.Add("X-Forwarded-Host", "indexer.example.org");
        req.Headers.Add("X-Forwarded-Proto", "https");

        var item = (await Xml(await client.SendAsync(req))).Descendants("item").Single();

        item.Element("enclosure")!.Attribute("url")!.Value
            .ShouldBe("https://indexer.example.org/newznab/api?t=get&id=AAAAAAAAAAAAAAAAAAAAAA&apikey=secret");
        item.Element("comments")!.Value.ShouldBe("https://indexer.example.org/release/AAAAAAAAAAAAAAAAAAAAAA");
    }

    [Fact]
    public async Task Poisoned_names_do_not_break_feed_or_get()
    {
        await Seed(null,
            Hit("AAAAAAAAAAAAAAAAAAAAAA", "Bad\u0001Title \ud800", author: "Au\u0003thor", book: "Bo\u0004ok"),
            Hit("BBBBBBBBBBBBBBBBBBBBBB", "Fine"));
        using var client = _factory.CreateClient();

        var feed = await client.GetAsync("/newznab/api?t=book");
        feed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Xml(feed)).Descendants("item").Count().ShouldBe(2);

        var get = await client.GetAsync("/newznab/api?t=get&id=AAAAAAAAAAAAAAAAAAAAAA");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        StubNzb.ReadHitId(await get.Content.ReadAsStreamAsync()).ShouldBe("AAAAAAAAAAAAAAAAAAAAAA");
    }
}
