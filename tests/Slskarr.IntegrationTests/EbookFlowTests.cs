using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Slskarr.Search;
using Slskarr.Tests;

namespace Slskarr.IntegrationTests;

/// <summary>
/// The whole ebook flow over HTTP only: Chaptarr-shaped requests in, slskd-shaped requests out to
/// <see cref="FakeSlskd"/>. The real background services run on the shared fake clock.
/// </summary>
public sealed class EbookFlowTests : IDisposable
{
    private const string ApiKey = "integration-key";
    private const string Author = "Frank Herbert";
    private const string Title = "Dune";
    private const string Peer = "bookworm";
    private const string DuneEpub = @"@@abcde\Books\Frank Herbert\Frank Herbert - Dune (1965).epub";
    private const string DuneMobi = @"@@abcde\Books\Frank Herbert\Frank Herbert - Dune (1965).mobi";
    private const string DuneEpubFile = "Frank Herbert - Dune (1965).epub";
    private const long Size = 1_048_576;
    private const string SearchesPath = "/api/v0/searches";
    private const string BatchesPath = "/api/v0/transfers/downloads/batches";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5); // Slskarr's default DownloadOptions.PollInterval
    private static readonly TimeSpan PollGrace = TimeSpan.FromMilliseconds(200);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "slskarr-integration", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeSlskd _slskd;
    private readonly Dictionary<string, string?> _config = new()
    {
        ["Slskarr:ApiKey"] = ApiKey,
        ["Slskarr:Slskd:DownloadsPath"] = "/downloads",
    };

    private SlskarrFactory? _factory;
    private HttpClient? _http;

    public EbookFlowTests()
    {
        _slskd = new FakeSlskd(_time);
        _slskd.AddPeer(Peer, [(DuneEpub, Size), (DuneMobi, Size)], freeSlot: true, queueLength: 0, speed: 1_000_000);
    }

    [Fact]
    public async Task Interactive_search_grab_download_import_delete()
    {
        _slskd.SearchDelay = TimeSpan.FromSeconds(3);
        _slskd.ScriptTransfer(_ =>
        [
            new(TimeSpan.Zero, "Queued, Remotely", PlaceInQueue: 3),
            new(TimeSpan.FromSeconds(20), "InProgress", Bytes: Size / 2),
            new(TimeSpan.FromSeconds(40), "Completed, Succeeded", Bytes: Size),
        ]);
        var chaptarr = Start();

        var items = await chaptarr.SearchBookAsync(Author, Title);
        chaptarr.SearchTiersSent.ShouldBe(1);
        var top = items[0];
        top.Title.ShouldContain("Dune");
        top.Title.ShouldContain("EPUB");
        var nzb = await chaptarr.DownloadNzbAsync(top.Enclosure);
        var nzoId = await chaptarr.AddFileAsync(nzb, top.Title + ".nzb");
        var jobId = nzoId["SLSKARR_".Length..];

        var seen = new List<string>();
        await PollUntil(async () =>
        {
            var slot = (await chaptarr.QueueAsync()).SingleOrDefault(s => s.NzoId == nzoId);
            if (slot is not null && (seen.Count == 0 || seen[^1] != slot.Status))
            {
                seen.Add(slot.Status);
            }

            return slot is null;
        }, "the download leaves the queue");
        seen.ShouldBe(["Queued", "Downloading"]);

        var done = (await chaptarr.HistoryAsync()).ShouldHaveSingleItem();
        done.NzoId.ShouldBe(nzoId);
        done.Status.ShouldBe("Completed");
        done.Storage.ShouldEndWith($"slskarr/{jobId}/{DuneEpubFile}");
        done.Storage.ShouldBe("/downloads/" + _slskd.DownloadedFiles.ShouldHaveSingleItem()); // where slskd put it

        await chaptarr.RemoveFromHistoryAsync(nzoId, deleteData: true, failed: false);
        (await chaptarr.HistoryAsync()).ShouldBeEmpty();
        _slskd.DownloadedFiles.ShouldBeEmpty();

        await UntilSearchesSettled();
        Posts(SearchesPath).Count.ShouldBe(1);
        Destination(Posts(BatchesPath).ShouldHaveSingleItem()).ShouldBe($"slskarr/{jobId}");
    }

    [Fact]
    public async Task Tiered_search_makes_one_soulseek_search()
    {
        await StartWithSlowSearch();

        // A search queued by any later tier would run once the first one finishes; wait until nothing is left.
        await UntilSearchesSettled();

        var search = Posts(SearchesPath).ShouldHaveSingleItem();
        JsonDocument.Parse(search.Body).RootElement.GetProperty("searchText").GetString().ShouldBe("Frank Herbert Dune");
    }

    [Fact]
    public async Task Late_results_arrive_via_rss()
    {
        var chaptarr = await StartWithSlowSearch();

        await AdvanceUntil(async () => (await chaptarr.RssAsync()).Count > 0, "late hits reach the RSS feed");
        await UntilSearchesSettled();

        var rss = await chaptarr.RssAsync();
        rss.Select(i => i.Title).ShouldContain(t => t.Contains("Dune") && t.Contains("EPUB"));
        rss.Select(i => i.Title).ShouldContain(t => t.Contains("Dune") && t.Contains("MOBI"));
        Posts(SearchesPath).Count.ShouldBe(1); // RSS polls never search
    }

    [Fact]
    public async Task Failed_download_reported_then_retry()
    {
        var attempts = 0;
        _slskd.ScriptTransfer(_ => Interlocked.Increment(ref attempts) == 1
            ?
            [
                new(TimeSpan.Zero, "Queued, Remotely"),
                new(TimeSpan.FromSeconds(10), "InProgress", Bytes: Size / 4),
                new(TimeSpan.FromSeconds(20), "Completed, Errored", Bytes: Size / 4),
            ]
            :
            [
                new(TimeSpan.Zero, "Queued, Remotely"),
                new(TimeSpan.FromSeconds(10), "InProgress", Bytes: Size / 2),
                new(TimeSpan.FromSeconds(20), "Completed, Succeeded", Bytes: Size),
            ]);
        var chaptarr = Start();
        var nzoId = await GrabTopAsync(chaptarr);

        await PollUntil(async () => (await chaptarr.HistoryAsync()).Any(s => s.NzoId == nzoId), "the failure reaches history");
        var failed = (await chaptarr.HistoryAsync()).ShouldHaveSingleItem();
        failed.Status.ShouldBe("Failed");
        failed.FailMessage.ShouldContain("Errored");

        (await chaptarr.RetryAsync(nzoId)).ShouldBe(nzoId);
        (await chaptarr.QueueAsync()).ShouldContain(s => s.NzoId == nzoId);
        (await chaptarr.HistoryAsync()).ShouldBeEmpty();

        await PollUntil(async () => (await chaptarr.HistoryAsync()).Any(s => s.NzoId == nzoId), "the retry finishes");
        var done = (await chaptarr.HistoryAsync()).ShouldHaveSingleItem();
        done.Status.ShouldBe("Completed");
        done.FailMessage.ShouldBe("");

        var batches = Posts(BatchesPath);
        batches.Count.ShouldBe(2);
        batches.Select(BatchId).Distinct().Count().ShouldBe(2);
        batches.Select(Destination).Distinct().ShouldHaveSingleItem().ShouldBe("slskarr/" + nzoId["SLSKARR_".Length..]);
    }

    [Fact]
    public async Task Restart_resumes_active_job()
    {
        _slskd.ScriptTransfer(_ =>
        [
            new(TimeSpan.Zero, "Queued, Remotely"),
            new(TimeSpan.FromSeconds(10), "InProgress", Bytes: Size / 10),
            new(TimeSpan.FromSeconds(60), "InProgress", Bytes: Size / 2),
            new(TimeSpan.FromSeconds(120), "Completed, Succeeded", Bytes: Size),
        ]);
        var chaptarr = Start();
        var nzoId = await GrabTopAsync(chaptarr);
        await PollUntil(async () => (await chaptarr.QueueAsync()).Any(s => s.NzoId == nzoId && s.Status == "Downloading"),
            "the transfer is in progress");

        chaptarr = Restart();

        (await chaptarr.QueueAsync()).ShouldContain(s => s.NzoId == nzoId);
        await PollUntil(async () => (await chaptarr.HistoryAsync()).Any(s => s.NzoId == nzoId), "the monitor completes the job");
        (await chaptarr.HistoryAsync()).ShouldHaveSingleItem().Status.ShouldBe("Completed");
        Posts(BatchesPath).Count.ShouldBe(1);
    }

    public void Dispose()
    {
        _http?.Dispose();
        _factory?.Dispose();
        _slskd.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private ChaptarrClient Start()
    {
        _factory = new SlskarrFactory(_dataPath, _time)
            .WithConfig(c =>
            {
                foreach (var (key, value) in _config)
                {
                    c[key] = value;
                }
            })
            .WithSlskdHandler(_slskd.CreateHandler);
        _http = _factory.CreateClient();
        return new ChaptarrClient(_http, ApiKey, _time);
    }

    /// <summary>Stops the host and starts a new one on the same database, clock and slskd.</summary>
    private ChaptarrClient Restart()
    {
        _http?.Dispose();
        _factory?.Dispose();
        return Start();
    }

    /// <summary>The search outlasts the sync wait, so every tier comes back empty.</summary>
    private async Task<ChaptarrClient> StartWithSlowSearch()
    {
        _slskd.SearchDelay = TimeSpan.FromSeconds(90);
        _config["Slskarr:Search:SyncWait"] = "00:00:02";
        _config["Slskarr:Search:SlskdSearchTimeoutSeconds"] = "90"; // slskd keeps the search open about this long
        var chaptarr = Start();

        (await chaptarr.SearchBookAsync(Author, Title)).ShouldBeEmpty();
        chaptarr.SearchTiersSent.ShouldBe(5);
        return chaptarr;
    }

    private static async Task<string> GrabTopAsync(ChaptarrClient chaptarr)
    {
        var top = (await chaptarr.SearchBookAsync(Author, Title))[0];
        return await chaptarr.AddFileAsync(await chaptarr.DownloadNzbAsync(top.Enclosure), top.Title + ".nzb");
    }

    /// <summary>Moves fake time on in 1s steps until <paramref name="done"/>; a real-time guard fails a hang fast.</summary>
    private async Task AdvanceUntil(Func<Task<bool>> done, string because)
    {
        var deadline = DateTime.UtcNow + Guard;
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out at fake {_time.GetUtcNow():O} waiting until {because}");
            }

            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Like <see cref="AdvanceUntil"/>, but moves fake time on only to the download monitor's next poll and checks
    /// <paramref name="done"/> between polls, so no state the monitor passes through is skipped however slowly
    /// the host runs. Time goes beyond the poll interval only when the poll does not come (the monitor re-armed
    /// its timer late, or has nothing left to poll because the job just finished).
    /// </summary>
    private async Task PollUntil(Func<Task<bool>> done, string because)
    {
        var deadline = DateTime.UtcNow + Guard;
        while (!await done())
        {
            var before = BatchPolls();
            var advanced = TimeSpan.Zero;
            while (BatchPolls() == before)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Timed out at fake {_time.GetUtcNow():O} waiting until {because}");
                }

                if (advanced < PollInterval)
                {
                    _time.Advance(TimeSpan.FromSeconds(1));
                    advanced += TimeSpan.FromSeconds(1);
                    await Task.Delay(2);
                    continue;
                }

                var wait = DateTime.UtcNow + PollGrace;
                while (BatchPolls() == before && DateTime.UtcNow < wait)
                {
                    await Task.Delay(2);
                }

                if (BatchPolls() == before)
                {
                    if (await done())
                    {
                        return;
                    }

                    _time.Advance(TimeSpan.FromSeconds(1));
                }
            }
        }
    }

    private int BatchPolls() =>
        _slskd.Requests.Count(r => r.Method == "GET" && r.Path.StartsWith(BatchesPath + "/", StringComparison.Ordinal));

    /// <summary>
    /// Advances until the first slskd search has finished and the search worker has nothing queued or running,
    /// so a count of searches taken afterwards includes everything the worker was ever going to send.
    /// </summary>
    private Task UntilSearchesSettled() => AdvanceUntil(() =>
    {
        var finished = _slskd.Requests.Any(r => r.Method == "DELETE" && r.Path.StartsWith(SearchesPath + "/", StringComparison.Ordinal));
        var status = _factory!.Services.GetRequiredService<SearchCoordinator>().Status();
        return Task.FromResult(finished && status is { Queued: 0, Running: null });
    }, "the search worker is idle");

    private List<FakeSlskd.RecordedRequest> Posts(string path) =>
        [.. _slskd.Requests.Where(r => r.Method == "POST" && r.Path == path)];

    private static string? Destination(FakeSlskd.RecordedRequest batch) =>
        JsonDocument.Parse(batch.Body).RootElement.GetProperty("options").GetProperty("destination").GetString();

    private static string? BatchId(FakeSlskd.RecordedRequest batch) =>
        JsonDocument.Parse(batch.Body).RootElement.GetProperty("id").GetString();
}
