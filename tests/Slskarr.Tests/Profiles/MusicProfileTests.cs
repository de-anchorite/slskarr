using Microsoft.Extensions.Options;
using Shouldly;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Profiles;
using Slskarr.Profiles.Ebook;
using Slskarr.Profiles.Music;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Profiles;

public class MusicProfileTests
{
    private static readonly IReadOnlyList<int> NoCats = [];

    private static SlskarrOptions Options()
    {
        var o = new SlskarrOptions();
        o.Ebook.Extensions = [.. EbookProfileOptions.DefaultExtensions];
        o.Ebook.FormatPreference = [.. EbookProfileOptions.DefaultFormatPreference];
        o.Ebook.Ranking = [.. EbookProfileOptions.AllowedRankingCriteria];
        o.Music.Extensions = [.. MusicProfileOptions.DefaultExtensions];
        o.Music.FormatPreference = [.. MusicProfileOptions.DefaultFormatPreference];
        o.Music.Ranking = [.. EbookProfileOptions.AllowedRankingCriteria];
        return o;
    }

    private static MusicProfile Create(Action<MusicProfileOptions>? configure = null)
    {
        var o = Options();
        configure?.Invoke(o.Music);
        return new MusicProfile(Microsoft.Extensions.Options.Options.Create(o));
    }

    private static EbookProfile Ebook() =>
        new(Microsoft.Extensions.Options.Options.Create(Options()), new EbookNameParser());

    private static SlskdFile F(string path, long size = 5_000_000, int? length = 200, int? bitRate = null) =>
        new(path, size, null, bitRate, null, 44100, length);

    private static SlskdSearchResponse Resp(params SlskdFile[] files) => new("peer", true, 0, 1000, files);

    private static string Q(string? artist, string? album, string? q = null) =>
        Create().BuildQuery(new SearchRequest(q, null, null, NoCats, artist, album));

    // Query

    [Fact]
    public void Query_removes_bracketed_album_segments() => Q("Radiohead", "OK Computer (Deluxe Edition)").ShouldBe("Radiohead OK Computer");

    [Fact]
    public void Query_various_artists_uses_album_only() => Q("Various Artists", "Now 42").ShouldBe("Now 42");

    [Fact]
    public void Query_va_uses_album_only() => Q("VA", "Now 42").ShouldBe("Now 42");

    [Fact]
    public void Query_artist_only() => Q("Radiohead", null).ShouldBe("Radiohead");

    [Fact]
    public void Query_falls_back_to_unbracketed_removal_when_album_is_all_brackets() => Q("Prince", "[Untitled]").ShouldBe("Prince Untitled");

    [Fact]
    public void Query_q_only_strips_brackets() => Q(null, null, "Radiohead OK Computer [Remastered]").ShouldBe("Radiohead OK Computer");

    [Fact]
    public void Tier_keys_match()
    {
        var music = Create();
        var tier1 = SearchKey.For(music, new SearchRequest(null, null, null, NoCats, "Radiohead", "OK Computer", "music"));
        var tier2 = SearchKey.For(music, new SearchRequest("Radiohead OK Computer", null, null, [3000], Function: "search"));
        tier1.Value.ShouldBe(tier2.Value);
    }

    [Theory]
    [InlineData("VA", "Now 42", "VA Now 42")]
    [InlineData("VA", "Now 42", "Various Artists Now 42")]
    [InlineData("Beatles", "Abbey Road", "Beatles Abbey Road")]
    public void Tier_keys_match_for_lidarr_text(string artist, string album, string q)
    {
        var music = Create();
        SearchKey.For(music, new SearchRequest(null, null, null, NoCats, artist, album, "music")).Value
            .ShouldBe(SearchKey.For(music, new SearchRequest(q, null, null, [3000], Function: "search")).Value);
    }

    // Lidarr self-titled: tier 1 artist=W&album=W&year=Y, tier 2 q="W W Y". One search key, one Soulseek query "W".
    [Theory]
    [InlineData("Weezer", "Weezer Weezer 1994", "Weezer")]
    [InlineData("Led Zeppelin", "Led Zeppelin Led Zeppelin 1969", "Led Zeppelin")]
    [InlineData("Weezer", "weezer WEEZER", "weezer")]
    public void Self_titled_tiers_share_key_and_query(string artist, string q, string expected)
    {
        var music = Create();
        var tier1 = new SearchRequest(null, null, null, NoCats, artist, artist, "music");
        var tier2 = new SearchRequest(q, null, null, [3000], Function: "search");

        SearchKey.For(music, tier1).Value.ShouldBe(SearchKey.For(music, tier2).Value);
        music.BuildQuery(tier2).ShouldBe(expected);
        music.BuildQuery(tier1).ShouldBe(artist);
    }

    [Fact]
    public void Self_titled_key_is_the_artist_alone() =>
        SearchKey.For(Create(), new SearchRequest("Weezer Weezer 1994", null, null, [3000], Function: "search")).Value.ShouldBe("music:weezer");

    [Theory]
    [InlineData("Radiohead OK Computer 1997")]
    [InlineData("Weezer Pinkerton")]
    [InlineData("Weezer Weezer Weezer")]
    [InlineData("Weezer 1994")]
    public void Non_self_titled_q_is_unchanged(string q) => Q(null, null, q).ShouldBe(q);

    [Fact]
    public void Query_va_prefix_is_kept_when_nothing_else_remains() => Q(null, null, "VA").ShouldBe("VA");

    [Fact]
    public void Ebook_book_function_still_requires_book_categories()
    {
        Ebook().Handles(Req("book", null, 3030)).ShouldBeFalse();
        Ebook().Handles(Req("book", null, 7020)).ShouldBeTrue();
    }

    [Fact]
    public void Artist_and_album_request_is_not_rss() =>
        new SearchRequest(null, null, null, NoCats, "A", "B").IsRss.ShouldBeFalse();

    // Grouping

    [Fact]
    public void Two_disc_folders_fold_into_one_candidate()
    {
        var c = Create().ToCandidates([Resp(
            F(@"M\Artist\Album\CD1\01.flac"), F(@"M\Artist\Album\CD1\02.flac"),
            F(@"M\Artist\Album\CD2\01.flac"))]).ShouldHaveSingleItem();

        c.RemotePath.ShouldBe(@"M\Artist\Album");
        c.Files.Count.ShouldBe(3);
        c.Files.Select(f => f.Subdir).Distinct().Order().ShouldBe(["CD1", "CD2"]);
        c.Size.ShouldBe(15_000_000);
        c.Name.Format.ShouldBe("FLAC");
        c.Name.Author.ShouldBe("Artist");
        c.ReleaseTitle.ShouldBe("Artist - Album [FLAC]");
    }

    [Fact]
    public void Disc_folder_names_are_recognised()
    {
        var c = Create().ToCandidates([Resp(F(@"M\A\B\Disc 1\1.mp3"), F(@"M\A\B\disk2 - bonus\1.mp3"))]);
        c.ShouldHaveSingleItem().Files.Select(f => f.Subdir).Order().ShouldBe(["Disc 1", "disk2 - bonus"]);
    }

    [Fact]
    public void Flac_and_mp3_in_one_folder_make_two_candidates()
    {
        var c = Create().ToCandidates([Resp(F(@"M\A\B\1.flac"), F(@"M\A\B\1.mp3"))]);
        c.Count.ShouldBe(2);
        c.Select(x => x.Name.Format).Order().ShouldBe(["FLAC", "MP3"]);
        c.ShouldAllBe(x => x.Files.Count == 1);
    }

    [Fact]
    public void Non_audio_is_ignored_and_other_folders_are_separate_candidates()
    {
        var c = Create().ToCandidates([Resp(
            F(@"M\A\B\01.flac"), F(@"M\A\B\Scans\cover.jpg"), F(@"M\A\B\Bonus\01.flac"))]);

        c.Count.ShouldBe(2);
        c.Select(x => x.RemotePath).Order().ShouldBe([@"M\A\B", @"M\A\B\Bonus"]);
    }

    [Fact]
    public void Non_audio_only_folder_gives_no_candidate() =>
        Create().ToCandidates([Resp(F(@"M\A\B\cover.jpg"), F(@"M\A\B\info.nfo"))]).ShouldBeEmpty();

    [Fact]
    public void Cue_with_single_long_audio_file_is_dropped() =>
        Create().ToCandidates([Resp(F(@"M\A\B\a.cue"), F(@"M\A\B\a.flac", length: 3000))]).ShouldBeEmpty();

    [Fact]
    public void Cue_with_twelve_tracks_is_kept()
    {
        var files = Enumerable.Range(1, 12).Select(i => F($@"M\A\B\{i:00}.flac")).Append(F(@"M\A\B\a.cue")).ToArray();
        Create().ToCandidates([Resp(files)]).ShouldHaveSingleItem().Files.Count.ShouldBe(12);
    }

    [Fact]
    public void Bin_is_dropped() =>
        Create().ToCandidates([Resp(F(@"M\A\B\a.bin"), F(@"M\A\B\a.cue"), F(@"M\A\B\1.flac"), F(@"M\A\B\2.flac"), F(@"M\A\B\3.flac"))]).ShouldBeEmpty();

    [Fact]
    public void Cue_with_two_long_audio_files_is_dropped() =>
        Create().ToCandidates([Resp(F(@"M\A\B\a.cue"), F(@"M\A\B\1.flac", length: 1800), F(@"M\A\B\2.flac", length: 1800))]).ShouldBeEmpty();

    [Fact]
    public void Release_size_limits_apply_to_total()
    {
        var p = Create(m => m.MinSizeBytes = 8_000_000);
        p.ToCandidates([Resp(F(@"M\A\B\1.flac"), F(@"M\A\B\2.flac"))]).Count.ShouldBe(1);
        p.ToCandidates([Resp(F(@"M\A\B\1.flac"))]).ShouldBeEmpty();
    }

    [Fact]
    public void Candidate_carries_peer_details_and_quality_tag()
    {
        var c = Create().ToCandidates([Resp(F(@"M\A\B\1.mp3", bitRate: 320), F(@"M\A\B\2.mp3", bitRate: 320))]).ShouldHaveSingleItem();
        c.Username.ShouldBe("peer");
        c.FreeSlot.ShouldBeTrue();
        c.ReleaseTitle.ShouldBe("A - B [MP3 320]");
    }

    // Handles

    private static SearchRequest Req(string? fn, string? q = null, params int[] cats) =>
        new(q, null, null, cats, Function: fn);

    [Fact]
    public void Handles_music_function_without_category()
    {
        var r = Req("music");
        Create().Handles(r).ShouldBeTrue();
        Ebook().Handles(r).ShouldBeFalse();
    }

    [Fact]
    public void Handles_search_with_audio_category()
    {
        var r = Req("search", "x", 3040);
        Create().Handles(r).ShouldBeTrue();
        Ebook().Handles(r).ShouldBeFalse();
    }

    [Fact]
    public void Audiobook_category_is_handled_by_neither()
    {
        var r = Req("search", "x", 3030);
        Create().Handles(r).ShouldBeFalse();
        Ebook().Handles(r).ShouldBeFalse();
    }

    [Fact]
    public void Handles_book_function_goes_to_ebook()
    {
        var r = Req("book");
        Ebook().Handles(r).ShouldBeTrue();
        Create().Handles(r).ShouldBeFalse();
    }

    [Fact]
    public void Plain_search_without_category_goes_to_ebook()
    {
        var r = Req("search", "dune");
        Ebook().Handles(r).ShouldBeTrue();
        Create().Handles(r).ShouldBeFalse();
    }

    // Interface members

    private static HitEntity Hit(string format) => new()
    {
        Id = "1", SearchKey = "k", Profile = "music", Username = "u", RemotePath = "p", ReleaseTitle = "t",
        Format = format, ParsedAuthor = "Radiohead", ParsedTitle = "OK Computer",
    };

    [Theory]
    [InlineData("FLAC", 3040)]
    [InlineData("ALAC", 3040)]
    [InlineData("WV", 3040)]
    [InlineData("MP3", 3010)]
    [InlineData("OGG", 3010)]
    public void Item_categories_by_format(string format, int sub) =>
        Create().ItemCategories(Hit(format)).ShouldBe([3000, sub]);

    [Fact]
    public void Music_metadata()
    {
        var p = Create();
        p.Name.ShouldBe("music");
        p.CoalesceBySuperset.ShouldBeFalse();
        p.SabCategory.ShouldBe("music");
        p.CapsCategory.Id.ShouldBe(3000);
        p.CapsCategory.Name.ShouldBe("Audio");
        p.CapsCategory.Subcats.ShouldBe([(3010, "Audio/MP3"), (3040, "Audio/Lossless")]);
        p.ItemAttributes(Hit("FLAC")).ShouldBe([("artist", "Radiohead"), ("album", "OK Computer")]);
        p.FormatRank("flac").ShouldBeLessThan(p.FormatRank("mp3"));
        p.FormatRank("xyz").ShouldBe(p.FormatRank("zzz"));
    }

    [Fact]
    public void Ebook_metadata_unchanged()
    {
        var p = Ebook();
        p.CoalesceBySuperset.ShouldBeTrue();
        p.CapsCategory.Id.ShouldBe(7000);
        p.CapsCategory.Subcats.ShouldBe([(7020, "Books/EBook")]);
        p.ItemCategories(Hit("EPUB")).ShouldBe([7000, 7020]);
        p.ItemAttributes(Hit("EPUB")).ShouldBe([("author", "Radiohead"), ("booktitle", "OK Computer")]);
    }
}
