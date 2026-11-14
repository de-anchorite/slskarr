using Shouldly;
using Slskarr.Data;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Search;

public sealed class SearchCoordinatorTests : IDisposable
{
    private static readonly SearchRequest AuthorTitle = new(null, "Frank Herbert", "Dune", [7020]);
    private static readonly SearchRequest TitleOnly = new(null, null, "Dune", [7020]);
    private static readonly SearchRequest QueryOnly = new("Dune", null, null, [7020]);
    private static readonly SearchRequest Rss = new(null, null, null, [7020]);

    private static readonly SlskdSearchResponse DunePeer = new("peer", true, 0, 1000,
    [
        new SlskdFile(@"@@abcde\Books\Frank Herbert\Frank Herbert - Dune (1965).epub", 1_000_000, "epub"),
        new SlskdFile(@"@@abcde\Books\Frank Herbert\Frank Herbert - Dune (1965).mobi", 1_000_000, "mobi"),
    ]);

    private SearchHarness _h = new();

    private string Key(SearchRequest r) => SearchKey.For(_h.Ebook, r).Value;

    private SearchHarness Use(Action<Slskarr.Configuration.SearchOptions> configure)
    {
        _h.Dispose();
        return _h = new SearchHarness(configure);
    }

    [Fact]
    public async Task Rss_never_starts_search()
    {
        _h.Slskd.Responses = [DunePeer];
        await _h.SearchAndRun(AuthorTitle);
        _h.Slskd.StartedTexts.Count.ShouldBe(1);

        _h.Time.Advance(TimeSpan.FromHours(1));
        var rss = await _h.Coordinator.SearchAsync(Rss, CancellationToken.None);

        rss.Count.ShouldBe(2);
        _h.Slskd.StartedTexts.Count.ShouldBe(1);
        _h.Jobs().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cached_result_returned_without_search()
    {
        _h.Slskd.Responses = [DunePeer];
        var first = await _h.SearchAndRun(AuthorTitle);
        first.Select(h => h.Format).ShouldBe(["EPUB", "MOBI"]);
        first.Select(h => h.Rank).ShouldBe([1, 2]);

        _h.Time.Advance(TimeSpan.FromHours(5));
        var second = await _h.Coordinator.SearchAsync(new SearchRequest("Frank Herbert Dune", null, null, [7020]), CancellationToken.None);

        second.Select(h => h.Id).ShouldBe(first.Select(h => h.Id));
        _h.Slskd.StartedTexts.ShouldBe(["Frank Herbert Dune"]);
        _h.Jobs().Single().State.ShouldBe(SearchJobState.Done);
    }

    [Fact]
    public async Task Title_only_tier_coalesces_with_author_title_job()
    {
        // Chaptarr: author+title comes back empty, then it tries title-only, then q=.
        (await _h.SearchAndRun(AuthorTitle)).ShouldBeEmpty();

        (await _h.Coordinator.SearchAsync(TitleOnly, CancellationToken.None)).ShouldBeEmpty();
        (await _h.Coordinator.SearchAsync(QueryOnly, CancellationToken.None)).ShouldBeEmpty();

        _h.Slskd.StartedTexts.Count.ShouldBe(1);
        _h.Jobs().Select(j => j.Key).ShouldBe([Key(AuthorTitle)]);
    }

    [Fact]
    public async Task Title_only_tier_waits_on_queued_author_title_job()
    {
        _h.Slskd.Responses = [DunePeer];
        var first = _h.Coordinator.SearchAsync(AuthorTitle, CancellationToken.None);
        await _h.UntilQueued(Key(AuthorTitle));
        var second = _h.Coordinator.SearchAsync(TitleOnly, CancellationToken.None);

        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();

        (await first).Count.ShouldBe(2);
        (await second).Select(h => h.Id).ShouldBe((await first).Select(h => h.Id));
        _h.Slskd.StartedTexts.Count.ShouldBe(1);
        _h.Jobs().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Late_results_returned_empty_then_available_via_Rss()
    {
        var h = Use(o => o.SlskdSearchTimeoutSeconds = 120);
        h.Slskd.Responses = [DunePeer];
        h.Slskd.CompleteAfter = TimeSpan.FromSeconds(90);

        var search = h.Coordinator.SearchAsync(AuthorTitle, CancellationToken.None);
        await h.UntilQueued(Key(AuthorTitle));
        var run = h.Worker.RunOnceAsync(CancellationToken.None);

        // Never reach 90s here, so the slskd search cannot have completed when SearchAsync returns.
        await h.AdvanceUntil(() => search.IsCompleted, stopAt: TimeSpan.FromSeconds(89));

        (await search).ShouldBeEmpty();
        (h.Time.GetUtcNow() - SearchHarness.Start).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));

        await h.AdvanceUntil(() => run.IsCompleted, stopAt: TimeSpan.FromSeconds(120));

        (await run).ShouldBeTrue();
        var recent = await h.Coordinator.RecentAsync(h.Ebook, 0, 100, CancellationToken.None);
        recent.Count.ShouldBe(2);
        h.Jobs().Single().State.ShouldBe(SearchJobState.Done);
        h.Slskd.Deleted.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Lifo_order()
    {
        var a = new SearchRequest(null, null, "Dune", [7020]);
        var b = new SearchRequest(null, null, "Emma", [7020]);
        var c = new SearchRequest(null, null, "Ulysses", [7020]);
        var pending = new List<Task<IReadOnlyList<HitEntity>>>();
        foreach (var r in new[] { a, b, c, a })
        {
            pending.Add(_h.Coordinator.SearchAsync(r, CancellationToken.None));
            await SearchHarness.Until(() => _h.Jobs().Any(j => j.Key == Key(r) && j.LastRequestedAt == _h.Time.GetUtcNow()), "requested");
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        _h.Jobs().Count(j => j.State == SearchJobState.Queued).ShouldBe(3);

        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse(); // min gap
        _h.Time.Advance(TimeSpan.FromSeconds(10));
        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
        _h.Time.Advance(TimeSpan.FromSeconds(10));
        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse();

        // a was re-requested last, so it jumped ahead of c and b.
        _h.Slskd.StartedTexts.ShouldBe(["Dune", "Ulysses", "Emma"]);
        await Task.WhenAll(pending);
    }

    [Fact]
    public async Task Expired_jobs_never_run()
    {
        var search = _h.Coordinator.SearchAsync(AuthorTitle, CancellationToken.None);
        await _h.UntilQueued(Key(AuthorTitle));

        _h.Time.Advance(TimeSpan.FromHours(25));
        (await search).ShouldBeEmpty();
        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse();

        _h.Slskd.StartedTexts.ShouldBeEmpty();
        var job = _h.Jobs().Single();
        job.State.ShouldBe(SearchJobState.Expired);
        job.CompletedAt.ShouldBe(_h.Time.GetUtcNow());
        _h.Coordinator.Status().ExpiredLast24h.ShouldBe(1);
    }

    [Fact]
    public async Task Search_returns_empty_when_slskd_unreachable()
    {
        _h.Slskd.StartThrows = new SlskdUnavailableException("slskd unreachable");

        var result = await _h.SearchAndRun(AuthorTitle);

        result.ShouldBeEmpty();
        var job = _h.Jobs().Single();
        job.State.ShouldBe(SearchJobState.Failed);
        job.Error.ShouldBe("slskd unreachable");
        job.CompletedAt.ShouldBe(_h.Time.GetUtcNow());
    }

    [Fact]
    public async Task Failed_job_requeued_only_after_5_minutes()
    {
        _h.Slskd.StartThrows = new SlskdUnavailableException("slskd unreachable");
        await _h.SearchAndRun(AuthorTitle);

        _h.Time.Advance(TimeSpan.FromMinutes(4));
        (await _h.Coordinator.SearchAsync(AuthorTitle, CancellationToken.None)).ShouldBeEmpty();
        _h.Jobs().Single().State.ShouldBe(SearchJobState.Failed);

        _h.Time.Advance(TimeSpan.FromMinutes(2));
        _h.Slskd.StartThrows = null;
        _h.Slskd.Responses = [DunePeer];
        (await _h.SearchAndRun(AuthorTitle)).Count.ShouldBe(2);
        var job = _h.Jobs().Single();
        job.State.ShouldBe(SearchJobState.Done);
        job.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Search_disappeared_from_slskd_marks_job_failed()
    {
        _h.Slskd.Disappear = true;

        (await _h.SearchAndRun(AuthorTitle)).ShouldBeEmpty();

        var job = _h.Jobs().Single();
        job.State.ShouldBe(SearchJobState.Failed);
        job.Error.ShouldBe("Search disappeared from slskd");
    }

    [Fact]
    public async Task Rerun_after_cache_ttl_keeps_hit_ids_and_refreshes_SeenAt()
    {
        _h.Slskd.Responses = [DunePeer];
        var first = await _h.SearchAndRun(AuthorTitle);

        _h.Time.Advance(TimeSpan.FromHours(7));
        var second = await _h.SearchAndRun(AuthorTitle);

        _h.Slskd.StartedTexts.Count.ShouldBe(2);
        second.Select(h => h.Id).ShouldBe(first.Select(h => h.Id));
        second.ShouldAllBe(h => h.SeenAt == _h.Time.GetUtcNow());
        first[0].Id.Length.ShouldBe(22);
        first[0].Id.ShouldNotContain('+');
        first[0].Id.ShouldNotContain('/');
        _h.Hits().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Restart_requeues_running_jobs_and_seeds_throttle()
    {
        var now = _h.Time.GetUtcNow();
        await using (var db = await _h.Db.CreateDbContextAsync())
        {
            db.SearchJobs.Add(Job(Key(TitleOnly), TitleOnly, SearchJobState.Done, now.AddSeconds(-5)));
            db.SearchJobs.Add(Job(Key(new SearchRequest(null, null, "Emma", [])), new SearchRequest(null, null, "Emma", []), SearchJobState.Running, null));
            await db.SaveChangesAsync();
        }

        var worker = _h.Restart();

        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse(); // last search 5s ago
        _h.Jobs().Single(j => j.Key == "ebook:emma").State.ShouldBe(SearchJobState.Queued);
        _h.Time.Advance(TimeSpan.FromSeconds(5));
        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
        _h.Slskd.StartedTexts.ShouldBe(["Emma"]);
    }

    [Fact]
    public async Task Retention_prunes_old_hits_except_linked_downloads()
    {
        var now = _h.Time.GetUtcNow();
        await using (var db = await _h.Db.CreateDbContextAsync())
        {
            db.Hits.AddRange(
                Hit("old", now.AddDays(-31)),
                Hit("fresh", now.AddDays(-29)),
                Hit("downloading", now.AddDays(-31)),
                Hit("deleted", now.AddDays(-31)));
            db.DownloadJobs.AddRange(Download("downloading", DownloadState.Downloading), Download("deleted", DownloadState.Deleted));
            await db.SaveChangesAsync();
        }

        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse();

        _h.Hits().Select(h => h.Id).ShouldBe(["downloading", "fresh"], ignoreOrder: true);
    }

    [Fact]
    public async Task Status_reports_queue()
    {
        _ = _h.Coordinator.SearchAsync(AuthorTitle, CancellationToken.None);
        await _h.UntilQueued(Key(AuthorTitle));
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        var status = _h.Coordinator.Status();

        status.Queued.ShouldBe(1);
        status.Running.ShouldBeNull();
        status.OldestAge.ShouldBe(TimeSpan.FromSeconds(30));
        status.ExpiredLast24h.ShouldBe(0);
        status.ThrottledUntilNextHour.ShouldBeFalse();
    }

    [Fact]
    public async Task Background_loop_runs_signalled_jobs()
    {
        _h.Slskd.Responses = [DunePeer];
        await _h.Worker.StartAsync(CancellationToken.None);
        try
        {
            (await _h.Coordinator.SearchAsync(AuthorTitle, CancellationToken.None)).Count.ShouldBe(2);

            // Throttled by the min gap: the loop sleeps on fake time, then runs it.
            var next = _h.Coordinator.SearchAsync(TitleOnly with { Title = "Emma" }, CancellationToken.None);
            await _h.UntilQueued("ebook:emma");
            await _h.AdvanceUntil(() => next.IsCompleted, stopAt: TimeSpan.FromSeconds(20));

            (await next).Count.ShouldBe(2);
            _h.Slskd.StartedTexts.ShouldBe(["Frank Herbert Dune", "Emma"]);
        }
        finally
        {
            await _h.Worker.StopAsync(CancellationToken.None);
        }
    }

    private static SearchJobEntity Job(string key, SearchRequest r, SearchJobState state, DateTimeOffset? completedAt) => new()
    {
        Key = key,
        Profile = "ebook",
        QueryText = SearchHarness.Profile().BuildQuery(r),
        RequestJson = System.Text.Json.JsonSerializer.Serialize(r),
        State = state,
        CreatedAt = SearchHarness.Start.AddMinutes(-1),
        LastRequestedAt = SearchHarness.Start.AddMinutes(-1),
        CompletedAt = completedAt,
    };

    private static HitEntity Hit(string id, DateTimeOffset seenAt) => new()
    {
        Id = id,
        SearchKey = "ebook:dune",
        Profile = "ebook",
        Username = "peer",
        RemotePath = $@"@@abcde\{id}.epub",
        ReleaseTitle = id,
        Format = "EPUB",
        SeenAt = seenAt,
    };

    private static DownloadJobEntity Download(string hitId, DownloadState state) => new()
    {
        Id = Guid.NewGuid(),
        HitId = hitId,
        Category = "ebooks",
        Title = hitId,
        Username = "peer",
        RemotePath = $@"@@abcde\{hitId}.epub",
        State = state,
    };

    public void Dispose() => _h.Dispose();
}
