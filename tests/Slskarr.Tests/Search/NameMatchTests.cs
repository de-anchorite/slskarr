using Shouldly;
using Slskarr.Profiles;
using Slskarr.Search;

namespace Slskarr.Tests.Search;

public class NameMatchTests
{
    private static readonly SearchRequest Dune = new(null, "Frank Herbert", "Dune", []);

    private static ParsedName Name(string? author, string? title) =>
        new(author, title, null, "EPUB", $"{author} - {title}");

    [Fact]
    public void NameMatch_buckets()
    {
        NameMatch.Score(Name("Frank Herbert", "Dune"), Dune).ShouldBe(MatchBucket.Strong);
        // Title present but author missing: not Strong; 1 of 3 tokens is below 60%.
        NameMatch.Score(Name("Someone Else", "Dune"), Dune).ShouldBe(MatchBucket.Weak);
        // Author + title partially: 2 of 3 tokens (67%) is Partial.
        NameMatch.Score(Name("Frank Herbert", "Children of Dune Messiah"), new SearchRequest(null, "Frank Herbert", "Dune Messiah Extra", []))
            .ShouldBe(MatchBucket.Partial);
        NameMatch.Score(Name("Frank Smith", "Heretics"), Dune).ShouldBe(MatchBucket.Weak);
        NameMatch.Score(Name("Jane Austen", "Emma"), Dune).ShouldBe(MatchBucket.None);
    }

    [Fact]
    public void NameMatch_folds_diacritics() =>
        NameMatch.Score(Name("Émile Zola", "Germinal"), new SearchRequest(null, "Emile Zola", "Germinal", []))
            .ShouldBe(MatchBucket.Strong);

    [Fact]
    public void Query_only_request_uses_query_tokens_as_title() =>
        NameMatch.Score(Name(null, "Dune Messiah"), new SearchRequest("dune messiah", null, null, []))
            .ShouldBe(MatchBucket.Strong);

    [Fact]
    public void Falls_back_to_stem_when_unparsed() =>
        NameMatch.Score(new ParsedName(null, null, null, "EPUB", "Frank Herbert - Dune"), Dune)
            .ShouldBe(MatchBucket.Strong);

    [Fact]
    public void Empty_title_tokens_never_strong() =>
        NameMatch.Score(Name("Frank Herbert", "The"), new SearchRequest(null, "Frank Herbert", "The", []))
            .ShouldBe(MatchBucket.Partial);

    [Fact]
    public void Rss_scores_none() =>
        NameMatch.Score(Name("Frank Herbert", "Dune"), new SearchRequest(null, null, null, [])).ShouldBe(MatchBucket.None);

    [Fact]
    public void Tokens_lowercase_strip_stopwords_keep_non_latin() =>
        NameMatch.Tokens("The Brothers Karamazov, and Достоевский 東京!")
            .ShouldBe(new[] { "brothers", "karamazov", "достоевскии", "東京" }, ignoreOrder: true);
}
