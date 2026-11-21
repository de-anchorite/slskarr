using Shouldly;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Search;

/// <summary>The search path logs what was asked, what was sent to Soulseek and a summary of what came back.</summary>
public sealed class SearchLoggingTests : IDisposable
{
    private static readonly SearchRequest Dune = new(null, "Frank Herbert", "Dune", [7020], Function: "book");

    private static readonly SlskdSearchResponse DunePeer = new("peer", true, 0, 1000,
    [
        new SlskdFile(@"@@abcde\Books\Frank Herbert\Frank Herbert - Dune (1965).epub", 1_000_000, "epub"),
        new SlskdFile(@"@@abcde\Books\Frank Herbert\notes.txt", 1_000, "txt"),
    ]);

    private readonly SearchHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task New_search_logs_query_and_result_summary()
    {
        _h.Slskd.Responses = [DunePeer];

        await _h.SearchAndRun(Dune);

        var lines = _h.Logs.Messages().ToList();
        lines.ShouldContain(l => l.Contains("Queued Soulseek search") && l.Contains("\"Frank Herbert Dune\""));
        lines.ShouldContain(l => l.Contains("Searching Soulseek for \"Frank Herbert Dune\""));
        var summary = lines.Single(l => l.Contains("Search \"Frank Herbert Dune\" done"));
        summary.ShouldContain("1 peer response(s)");
        summary.ShouldContain("2 file(s)");
        summary.ShouldContain("1 release(s)");
        lines.ShouldContain(l => l.Contains("#1") && l.Contains("Frank Herbert - Dune (1965)") && l.Contains("peer"));
    }

    [Fact]
    public async Task Search_with_no_usable_results_says_so()
    {
        _h.Slskd.Responses = [new SlskdSearchResponse("peer", true, 0, 1000, [new SlskdFile(@"@@a\x\notes.txt", 10, "txt")])];

        await _h.SearchAndRun(Dune);

        _h.Logs.Messages().ShouldContain(l => l.Contains("done") && l.Contains("1 peer response(s)") && l.Contains("0 release(s)"));
    }

    [Fact]
    public async Task Cached_search_logs_cache_hit()
    {
        _h.Slskd.Responses = [DunePeer];
        await _h.SearchAndRun(Dune);

        await _h.Coordinator.SearchAsync(Dune, CancellationToken.None).Within();

        _h.Logs.Messages().ShouldContain(l => l.Contains("cached") && l.Contains("1 release(s)"));
    }
}
