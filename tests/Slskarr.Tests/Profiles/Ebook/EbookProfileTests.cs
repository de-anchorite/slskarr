using Microsoft.Extensions.Options;
using Shouldly;
using Slskarr.Configuration;
using Slskarr.Profiles;
using Slskarr.Profiles.Ebook;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Profiles.Ebook;

public class EbookProfileTests
{
    private static EbookProfile Create()
    {
        var options = new SlskarrOptions
        {
            Ebook =
            {
                Extensions = [.. EbookProfileOptions.DefaultExtensions],
                FormatPreference = [.. EbookProfileOptions.DefaultFormatPreference],
                Ranking = [.. EbookProfileOptions.AllowedRankingCriteria],
            },
        };
        return new EbookProfile(Options.Create(options), new EbookNameParser());
    }

    private static SlskdSearchResponse Response(params SlskdFile[] files) => new("peer", true, 0, 1000, files);

    [Theory]
    [InlineData(new[] { 7000 }, true)]
    [InlineData(new[] { 7020 }, true)]
    [InlineData(new int[0], true)]
    [InlineData(new[] { 3030 }, false)]
    public void Handles_book_categories(int[] categories, bool expected) =>
        Create().Handles(new SearchRequest(null, null, null, categories)).ShouldBe(expected);

    [Fact]
    public void BuildQuery_joins_author_and_title() =>
        Create().BuildQuery(new SearchRequest(null, "Frank Herbert", "Dune", [])).ShouldBe("Frank Herbert Dune");

    [Fact]
    public void BuildQuery_uses_free_text_query() =>
        Create().BuildQuery(new SearchRequest("Dune Frank Herbert", null, null, [])).ShouldBe("Dune Frank Herbert");

    [Fact]
    public void BuildQuery_strips_punctuation_and_collapses_whitespace() =>
        Create().BuildQuery(new SearchRequest("Dune:  \"Messiah\"; (Part 1)! [a], what's?", null, null, []))
            .ShouldBe("Dune Messiah Part 1 a what s");

    [Fact]
    public void ToCandidates_drops_non_ebook_types()
    {
        var result = Create().ToCandidates([Response(
            new SlskdFile(@"a\b.mkv", 2_000_000, "mkv"),
            new SlskdFile(@"a\b.mp3", 2_000_000, "mp3"),
            new SlskdFile(@"a\b.zip", 2_000_000, "zip"),
            new SlskdFile(@"a\b.EPUB", 2_000_000, null))]);
        result.Count.ShouldBe(1);
        result[0].RemotePath.ShouldBe(@"a\b.EPUB");
        result[0].Profile.ShouldBe("ebook");
    }

    [Fact]
    public void ToCandidates_applies_size_bounds()
    {
        var result = Create().ToCandidates([Response(
            new SlskdFile(@"a\small.epub", 5_000, "epub"),
            new SlskdFile(@"a\huge.epub", 300_000_000, "epub"),
            new SlskdFile(@"a\ok.epub", 2_000_000, "epub"))]);
        result.Select(c => c.RemotePath).ShouldBe([@"a\ok.epub"]);
    }

    [Fact]
    public void ToCandidates_dedupes_same_peer_same_path()
    {
        var file = new SlskdFile(@"a\ok.epub", 2_000_000, "epub");
        var result = Create().ToCandidates([Response(file, file), Response(file)]);
        result.Count.ShouldBe(1);
    }

    [Fact]
    public void ToCandidates_keeps_same_path_on_different_peers()
    {
        var file = new SlskdFile(@"a\ok.epub", 2_000_000, "epub");
        var result = Create().ToCandidates([Response(file), new SlskdSearchResponse("other", false, 3, 5, [file])]);
        result.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(@"x\Books\Frank Herbert - Dune (1965).epub", "Frank Herbert - Dune (1965) [EPUB]")]
    [InlineData(@"x\Books\Frank Herbert - Dune.epub", "Frank Herbert - Dune [EPUB]")]
    [InlineData(@"x\Books\random-scan-0042.pdf", "random-scan-0042 [PDF]")]
    public void ReleaseTitle_format(string path, string expected) =>
        Create().ToCandidates([Response(new SlskdFile(path, 2_000_000, null))])[0].ReleaseTitle.ShouldBe(expected);

    [Fact]
    public void FormatRank_follows_preference()
    {
        var profile = Create();
        profile.FormatRank("epub").ShouldBeLessThan(profile.FormatRank("pdf"));
        profile.FormatRank("EPUB").ShouldBe(profile.FormatRank("epub"));
        profile.FormatRank("lit").ShouldBeGreaterThan(profile.FormatRank("cbr"));
    }

    [Fact]
    public void Registry_resolves_profiles()
    {
        var profile = Create();
        var registry = new ProfileRegistry([profile]);
        registry.ForRequest(new SearchRequest("x", null, null, [7020])).ShouldBe(profile);
        registry.ForRequest(new SearchRequest("x", null, null, [3030])).ShouldBeNull();
        registry.ForSabCategory("ebooks").ShouldBe(profile);
        registry.ByName("ebook").ShouldBe(profile);
        registry.ByName("nope").ShouldBeNull();
    }

    [Fact]
    public void Ranking_comes_from_options() =>
        Create().Ranking.ShouldBe(EbookProfileOptions.AllowedRankingCriteria);

    [Fact]
    public void Ebook_candidate_has_one_file()
    {
        var result = Create().ToCandidates([Response(new SlskdFile(@"a\b.epub", 2_000_000, "epub"))]);
        var c = result.ShouldHaveSingleItem();
        var f = c.Files.ShouldHaveSingleItem();
        f.RemotePath.ShouldBe(c.RemotePath);
        f.Subdir.ShouldBe("");
        f.Size.ShouldBe(c.Size);
    }
}
