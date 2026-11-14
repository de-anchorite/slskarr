using Shouldly;
using Slskarr.Search;

namespace Slskarr.Tests.Search;

public class SearchKeyTests
{
    [Fact]
    public void SearchKey_ignores_token_order_and_tiers()
    {
        var p = SearchHarness.Profile();
        var a = SearchKey.For(p, new SearchRequest("Dune Frank Herbert", null, null, []));
        var b = SearchKey.For(p, new SearchRequest("Frank Herbert Dune", null, null, []));
        var c = SearchKey.For(p, new SearchRequest(null, "Frank Herbert", "Dune", []));

        a.Value.ShouldBe("ebook:dune frank herbert");
        b.Value.ShouldBe(a.Value);
        c.Value.ShouldBe(a.Value);
        a.Tokens.ShouldBe(["dune", "frank", "herbert"], ignoreOrder: true);
        a.ShouldBe(c);

        var titleOnly = SearchKey.For(p, new SearchRequest(null, null, "Dune", []));
        titleOnly.Value.ShouldBe("ebook:dune");
        a.Tokens.IsSupersetOf(titleOnly.Tokens).ShouldBeTrue();
    }

    [Fact]
    public void TokensOf_reads_tokens_back_from_key_value()
    {
        SearchKey.TokensOf("ebook:dune frank herbert").ShouldBe(["dune", "frank", "herbert"], ignoreOrder: true);
        SearchKey.TokensOf("ebook:").ShouldBeEmpty();
    }
}
