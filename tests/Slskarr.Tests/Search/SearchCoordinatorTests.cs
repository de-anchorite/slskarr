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

    // Review focus: an artist-only request must not be served by (or coalesced onto) the artist+album job.
    [Fact]
    public async Task Artist_search_after_album_search_searches_again()
    {
        var album = new SearchRequest(null, null, null, [3000], "Radiohead", "OK Computer", "music");
        var artist = new SearchRequest(null, null, null, [3000], "Radiohead", null, "music");
        (await _h.SearchAndRun(album, _h.Music)).ShouldBeEmpty();
        _h.Jobs().Single().State.ShouldBe(SearchJobState.Done);

        _h.Time.Advance(TimeSpan.FromMinutes(5)); // past the throttle gap
        (await _h.SearchAndRun(artist, _h.Music)).ShouldBeEmpty();

        _h.Slskd.StartedTexts.ShouldBe(["Radiohead OK Computer", "Radiohead"]);
        _h.Jobs().Select(j => j.Key).Order().ShouldBe([SearchKey.For(_h.Music, album).Value, SearchKey.For(_h.Music, artist).Value]);
    }

    // Lidarr tier 2 (t=search&q=) arriving while the tier-1 (t=music) search is still running in slskd.
    [Fact]
    public async Task Lidarr_tier2_waits_on_running_tier1_job()
    {
        var h = Use(o => o.SlskdSearchTimeoutSeconds = 60);
        h.Slskd.CompleteAfter = TimeSpan.FromSeconds(30);
        var tier1 = new SearchRequest(null, null, null, [3000, 3010, 3040], "Radiohead", "OK Computer", "music");
        var tier2 = new SearchRequest("Radiohead OK Computer", null, null, [3000, 3010, 3040], Function: "search");
        var key = SearchKey.For(h.Music, tier1).Value;

        var first = h.Coordinator.SearchAsync(tier1, CancellationToken.None);
        await h.UntilQueued(key);
        var run = h.Worker.RunOnceAsync(CancellationToken.None);
        await SearchHarness.Until(() => h.Jobs().Single().State == SearchJobState.Running && h.Slskd.StartedTexts.Count == 1, "tier 1 running");

        var second = h.Coordinator.SearchAsync(tier2, CancellationToken.None);
        await Task.Delay(50); // let tier 2 reach its decision; it must not re-queue the running job
        h.Jobs().Single().State.ShouldBe(SearchJobState.Running);
        second.IsCompleted.ShouldBeFalse();
        await h.AdvanceUntil(() => run.IsCompleted && first.IsCompleted && second.IsCompleted, stopAt: TimeSpan.FromMinutes(2));

        (await run.Within()).ShouldBeTrue();
        await first.Within();
        await second.Within();
        h.Slskd.StartedTexts.ShouldBe(["Radiohead OK Computer"]);
        h.Jobs().Select(j => j.Key).ShouldBe([key]);
        h.Jobs().Single().State.ShouldBe(SearchJobState.Done);
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

        (await first.Within()).Count.ShouldBe(2);
        (await second.Within()).Select(h => h.Id).ShouldBe((await first.Within()).Select(h => h.Id));
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

        (await search.Within()).ShouldBeEmpty();
        (h.Time.GetUtcNow() - SearchHarness.Start).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));

        await h.AdvanceUntil(() => run.IsCompleted, stopAt: TimeSpan.FromSeconds(120));

        (await run.Within()).ShouldBeTrue();
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
        await Task.WhenAll(pending).Within();
    }

    [Fact]
    public async Task Expired_jobs_never_run()
    {
        var search = _h.Coordinator.SearchAsync(AuthorTitle, CancellationToken.None);
        await _h.UntilQueued(Key(AuthorTitle));

        // Expire before awaiting: the waiter's 60s timer may register after the advance, so the expiry
        // notification (not the timer) is what must release it.
        _h.Time.Advance(TimeSpan.FromHours(25));
        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse();
        (await search.Within()).ShouldBeEmpty();

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
        await using var check = await _h.Db.CreateDbContextAsync();
        check.HitFiles.Select(f => f.HitId).ShouldBe(["downloading", "fresh"], ignoreOrder: true); // cascaded
    }

    // Root cause of a flaky SABnzbd test: hits seeded by the download helpers had SeenAt = year 1, so the
    // factory's worker could prune them mid-test.
    [Fact]
    public async Task Download_harness_hits_survive_the_first_retention_prune()
    {
        await using (var db = await _h.Db.CreateDbContextAsync())
        {
            db.Hits.Add(Slskarr.Tests.Downloads.DownloadHarness.NewHit("seeded"));
            await db.SaveChangesAsync();
        }

        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse();

        _h.Hits().Select(h => h.Id).ShouldBe(["seeded"]);
    }

    [Fact]
    public async Task Retention_prunes_old_finished_search_jobs()
    {
        var now = _h.Time.GetUtcNow();
        await using (var db = await _h.Db.CreateDbContextAsync())
        {
            db.SearchJobs.AddRange(
                SearchJob("old-done", SearchJobState.Done, created: now.AddDays(-40), completed: now.AddDays(-31)),
                SearchJob("old-failed", SearchJobState.Failed, created: now.AddDays(-40), completed: now.AddDays(-31)),
                SearchJob("old-expired", SearchJobState.Expired, created: now.AddDays(-40), completed: now.AddDays(-31)),
                SearchJob("old-done-no-completion", SearchJobState.Done, created: now.AddDays(-31), completed: null),
                SearchJob("recent-done", SearchJobState.Done, created: now.AddDays(-40), completed: now.AddDays(-29)),
                SearchJob("recent-created", SearchJobState.Done, created: now.AddDays(-29), completed: null));
            await db.SaveChangesAsync();
        }

        (await _h.Worker.RunOnceAsync(CancellationToken.None)).ShouldBeFalse();

        _h.Jobs().Select(j => j.Key).ShouldBe(["recent-done", "recent-created"], ignoreOrder: true);
    }

    private static SearchJobEntity SearchJob(string key, SearchJobState state, DateTimeOffset created, DateTimeOffset? completed) => new()
    {
        Key = key,
        Profile = "ebook",
        QueryText = key,
        RequestJson = "{}",
        State = state,
        CreatedAt = created,
        LastRequestedAt = created,
        CompletedAt = completed,
    };

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

            (await next.Within()).Count.ShouldBe(2);
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
        Files = [new HitFileEntity { HitId = id, RemotePath = $@"@@abcde\{id}.epub" }],
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
