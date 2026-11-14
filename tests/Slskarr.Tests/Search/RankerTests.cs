using Microsoft.Extensions.Options;
using Shouldly;
using Slskarr.Configuration;
using Slskarr.Profiles;
using Slskarr.Profiles.Ebook;
using Slskarr.Search;

namespace Slskarr.Tests.Search;

public class RankerTests
{
    private static readonly string[] Default = [.. EbookProfileOptions.AllowedRankingCriteria];
    private static readonly SearchRequest Dune = new(null, "Frank Herbert", "Dune", []);

    private static EbookProfile Profile() =>
        new(Options.Create(new SlskarrOptions
        {
            Ebook =
            {
                Extensions = [.. EbookProfileOptions.DefaultExtensions],
                FormatPreference = [.. EbookProfileOptions.DefaultFormatPreference],
                Ranking = Default,
            },
        }), new EbookNameParser());

    private static ReleaseCandidate Hit(string user, string author, string title, bool free, int queue, int speed, string format = "EPUB") =>
        new("ebook", user, $"\\{author}\\{title}.{format.ToLowerInvariant()}", 1000,
            new ParsedName(author, title, null, format, $"{author} - {title}"), $"{author} - {title}", free, queue, speed);

    [Fact]
    public void Default_order_puts_name_match_first()
    {
        var strong = Hit("slow", "Frank Herbert", "Dune", false, 500, 1);
        var none = Hit("fast", "Jane Austen", "Emma", true, 0, 9999);
        new Ranker().Rank([none, strong], Dune, Profile(), Default, 50).ShouldBe([strong, none]);
    }

    [Fact]
    public void Custom_order_respected()
    {
        var strong = Hit("slow", "Frank Herbert", "Dune", false, 500, 1);
        var none = Hit("fast", "Jane Austen", "Emma", true, 0, 9999);
        new Ranker().Rank([strong, none], Dune, Profile(), ["FreeSlot", "NameMatch"], 50).ShouldBe([none, strong]);
    }

    [Fact]
    public void Cap_applies_after_ranking()
    {
        var fast = Enumerable.Range(0, 60).Select(i => Hit($"fast{i}", "Jane Austen", "Emma", true, 0, 9999));
        var strong = Hit("slow", "Frank Herbert", "Dune", false, 500, 1);
        var ranked = new Ranker().Rank(fast.Append(strong), Dune, Profile(), Default, 50);
        ranked.Count.ShouldBe(50);
        ranked[0].ShouldBeSameAs(strong);
    }

    [Fact]
    public void Remaining_criteria_break_ties()
    {
        var a = Hit("a", "Frank Herbert", "Dune", true, 5, 100, "PDF");
        var b = Hit("b", "Frank Herbert", "Dune", true, 5, 100, "EPUB");
        var c = Hit("c", "Frank Herbert", "Dune", true, 2, 50);
        var d = Hit("d", "Frank Herbert", "Dune", true, 5, 900);
        new Ranker().Rank([a, b, c, d], Dune, Profile(), Default, 50).ShouldBe([c, d, b, a]);
    }

    [Fact]
    public void Sort_is_stable_for_equal_hits()
    {
        var hits = Enumerable.Range(0, 20).Select(i => Hit($"u{i}", "Frank Herbert", "Dune", true, 0, 10)).ToList();
        new Ranker().Rank(hits, Dune, Profile(), Default, 50).ShouldBe(hits);
    }

    [Fact]
    public void Unknown_criterion_throws() =>
        Should.Throw<ArgumentException>(() => new Ranker().Rank([], Dune, Profile(), ["Bogus"], 50));
}
