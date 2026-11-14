using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Profiles;
using Slskarr.Profiles.Ebook;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Search;

/// <summary>Coordinator + worker over a temp SQLite db, a <see cref="FakeTimeProvider"/> and a <see cref="FakeSearchSlskd"/>.</summary>
public sealed class SearchHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slskarr-search-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public SearchHarness(Action<SearchOptions>? configure = null)
    {
        Directory.CreateDirectory(_dir);
        var options = DefaultOptions();
        configure?.Invoke(options.Search);
        Options = Microsoft.Extensions.Options.Options.Create(options);
        _services = new ServiceCollection()
            .AddDbContextFactory<SlskarrDb>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "slskarr.db")};Pooling=False"))
            .BuildServiceProvider();
        Db = _services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        using (var db = Db.CreateDbContext())
        {
            db.Database.Migrate();
        }

        Slskd = new FakeSearchSlskd(Time);
        Registry = new ProfileRegistry([Profile(Options)]);
        Throttle = new SearchThrottle(Options);
        Coordinator = new SearchCoordinator(Registry, Db, Options, Throttle, Time);
        Worker = NewWorker(Coordinator, Throttle);
    }

    public FakeTimeProvider Time { get; } = new(Start);

    public IOptions<SlskarrOptions> Options { get; }

    public IDbContextFactory<SlskarrDb> Db { get; }

    public FakeSearchSlskd Slskd { get; }

    public ProfileRegistry Registry { get; }

    public SearchThrottle Throttle { get; }

    public SearchCoordinator Coordinator { get; }

    public SearchWorker Worker { get; private set; }

    public IMediaProfile Ebook => Registry.All[0];

    public static SlskarrOptions DefaultOptions() => new()
    {
        Ebook =
        {
            Extensions = [.. EbookProfileOptions.DefaultExtensions],
            FormatPreference = [.. EbookProfileOptions.DefaultFormatPreference],
            Ranking = [.. EbookProfileOptions.AllowedRankingCriteria],
        },
    };

    public static EbookProfile Profile(IOptions<SlskarrOptions>? options = null) =>
        new(options ?? Microsoft.Extensions.Options.Options.Create(DefaultOptions()), new EbookNameParser());

    /// <summary>Simulates a process restart: new throttle, coordinator and worker over the same db.</summary>
    public SearchWorker Restart()
    {
        var throttle = new SearchThrottle(Options);
        Worker = NewWorker(new SearchCoordinator(Registry, Db, Options, throttle, Time), throttle);
        return Worker;
    }

    private SearchWorker NewWorker(SearchCoordinator coordinator, SearchThrottle throttle) =>
        new(coordinator, throttle, Registry, new Ranker(), Slskd, Db, Options, Time, NullLogger<SearchWorker>.Instance);

    public List<SearchJobEntity> Jobs()
    {
        using var db = Db.CreateDbContext();
        return [.. db.SearchJobs.AsNoTracking()];
    }

    public List<HitEntity> Hits()
    {
        using var db = Db.CreateDbContext();
        return [.. db.Hits.AsNoTracking()];
    }

    /// <summary>Waits (real time, short) until the condition holds; guards against a hung test.</summary>
    public static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}");
            }

            await Task.Delay(2);
        }
    }

    /// <summary>
    /// Advances fake time 1s at a time until <paramref name="done"/>, but never past <paramref name="stopAt"/>
    /// after <see cref="Start"/>; from there it only waits (real time), so work that needs no more fake time can finish.
    /// </summary>
    public async Task AdvanceUntil(Func<bool> done, TimeSpan stopAt)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out at fake +{Time.GetUtcNow() - Start}");
            }

            if (Time.GetUtcNow() - Start < stopAt)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
            }

            await Task.Delay(2);
        }
    }

    public Task UntilQueued(string key) =>
        Until(() => Jobs().Any(j => j.Key == key && j.State == SearchJobState.Queued), $"{key} is queued");

    /// <summary>Starts a search, lets the worker run the job it queues, and returns the search result.</summary>
    public async Task<IReadOnlyList<HitEntity>> SearchAndRun(SearchRequest request)
    {
        var key = SearchKey.For(Ebook, request).Value;
        var search = Coordinator.SearchAsync(request, CancellationToken.None);
        await Until(() => search.IsCompleted || Jobs().Any(j => j.Key == key && j.State == SearchJobState.Queued), "search queued or done");
        if (!search.IsCompleted)
        {
            (await Worker.RunOnceAsync(CancellationToken.None)).ShouldBeTrue();
        }

        return await search.Within();
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

/// <summary>Real-time guard so a hung wait fails fast instead of stalling the run.</summary>
public static class TaskGuard
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(Limit);

    public static Task Within(this Task task) => task.WaitAsync(Limit);
}

/// <summary>In-memory slskd search API. Searches complete <see cref="CompleteAfter"/> after they start (fake time).</summary>
public sealed class FakeSearchSlskd(TimeProvider time) : ISlskdClient
{
    private readonly Dictionary<Guid, DateTimeOffset> _started = [];

    public List<string> StartedTexts { get; } = [];

    public List<Guid> Deleted { get; } = [];

    public TimeSpan CompleteAfter { get; set; } = TimeSpan.Zero;

    public IReadOnlyList<SlskdSearchResponse> Responses { get; set; } = [];

    public Exception? StartThrows { get; set; }

    public bool Disappear { get; set; }

    public Task<Guid> StartSearchAsync(string text, int timeoutSeconds, int responseLimit, CancellationToken ct)
    {
        if (StartThrows is { } ex)
        {
            throw ex;
        }

        lock (_started)
        {
            StartedTexts.Add(text);
            var id = Guid.NewGuid();
            _started[id] = time.GetUtcNow();
            return Task.FromResult(id);
        }
    }

    public Task<SlskdSearch?> GetSearchAsync(Guid id, bool includeResponses, CancellationToken ct)
    {
        if (Disappear)
        {
            return Task.FromResult<SlskdSearch?>(null);
        }

        DateTimeOffset started;
        lock (_started)
        {
            started = _started[id];
        }

        var complete = time.GetUtcNow() >= started + CompleteAfter;
        var responses = complete ? Responses : [];
        return Task.FromResult<SlskdSearch?>(new SlskdSearch(
            id, complete ? "Completed, TimedOut" : "InProgress", complete, responses.Count,
            includeResponses ? responses : null));
    }

    public Task DeleteSearchAsync(Guid id, CancellationToken ct)
    {
        lock (_started)
        {
            Deleted.Add(id);
        }

        return Task.CompletedTask;
    }

    public Task<SlskdEnqueueResult> EnqueueBatchAsync(Guid batchId, string username, string remotePath, long size, string destination, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<SlskdBatch?> GetBatchAsync(Guid batchId, CancellationToken ct) => throw new NotSupportedException();

    public Task<int?> GetPlaceInQueueAsync(string username, Guid transferId, CancellationToken ct) => throw new NotSupportedException();

    public Task CancelDownloadAsync(string username, Guid transferId, bool remove, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetVersionAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SlskdLocalFile>?> ListDownloadDirAsync(string relativeDir, CancellationToken ct) => throw new NotSupportedException();

    public Task<SlskdDeleteResult> DeleteDownloadDirAsync(string relativeDir, CancellationToken ct) => throw new NotSupportedException();

    public Task<SlskdDeleteResult> DeleteIncompleteFileAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();
}
