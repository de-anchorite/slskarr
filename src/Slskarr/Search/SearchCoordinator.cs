using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Profiles;
using static System.FormattableString;

namespace Slskarr.Search;

public sealed record SearchQueueStatus(int Queued, string? Running, TimeSpan? OldestAge, int ExpiredLast24h, bool ThrottledUntilNextHour);

/// <summary>
/// Front door for searches: serves cached and coalesced results, queues new slskd searches for
/// <see cref="SearchWorker"/>, and waits a bounded time for them. The database is the source of truth.
/// </summary>
public sealed class SearchCoordinator(
    ProfileRegistry registry,
    IDbContextFactory<SlskarrDb> dbFactory,
    IOptions<SlskarrOptions> options,
    SearchThrottle throttle,
    TimeProvider time,
    ILogger<SearchCoordinator> logger)
{
    /// <summary>A failed job is not re-queued by requests within this long of its failure.</summary>
    internal static readonly TimeSpan FailedRetryAfter = TimeSpan.FromMinutes(5);

    private readonly SearchOptions _options = options.Value.Search;
    private readonly Channel<string> _signal = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new(StringComparer.Ordinal);

    // Serialises the read-decide-upsert step so concurrent identical requests can't insert one key twice.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Signalled with a job key whenever a job is queued or bumped.</summary>
    internal ChannelReader<string> Signals => _signal.Reader;

    public async Task<IReadOnlyList<HitEntity>> SearchAsync(SearchRequest r, CancellationToken ct)
    {
        var p = registry.ForRequest(r);
        if (p is null)
        {
            return [];
        }

        if (r.IsRss)
        {
            return await RecentAsync(p, 0, _options.ResultCap, ct);
        }

        var key = SearchKey.For(p, r);
        if (key.Tokens.Count == 0)
        {
            // Nothing searchable (only stop words or punctuation); never send that to Soulseek.
            logger.LogInformation("Nothing searchable in the {Profile} request; no Soulseek search", p.Name);
            return [];
        }

        var decision = await DecideAsync(p, r, key, ct);
        if (decision.Hits is { } ready)
        {
            return ready;
        }

        try
        {
            await decision.Completion!.WaitAsync(_options.SyncWait, time, ct);
        }
        catch (TimeoutException)
        {
            // Late results are picked up later through RSS.
            logger.LogInformation("Search {Key} still running after {SyncWait}; answering with what is stored, later results come through RSS",
                decision.JobKey, _options.SyncWait);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.SearchJobs.AsNoTracking().SingleAsync(j => j.Key == decision.JobKey, ct);
        return await HitsForAsync(db, job, ct);
    }

    public async Task<IReadOnlyList<HitEntity>> RecentAsync(IMediaProfile? p, int offset, int limit, CancellationToken ct)
    {
        var since = time.GetUtcNow() - _options.RssWindow;
        var profile = p?.Name;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Hits.AsNoTracking()
            .Where(h => (profile == null || h.Profile == profile) && h.SeenAt >= since)
            .OrderByDescending(h => h.SeenAt)
            .ThenBy(h => h.SearchKey)
            .ThenBy(h => h.Rank)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    public SearchQueueStatus Status()
    {
        var now = time.GetUtcNow();
        var dayAgo = now - TimeSpan.FromHours(24);
        using var db = dbFactory.CreateDbContext();
        var queued = db.SearchJobs.AsNoTracking().Where(j => j.State == SearchJobState.Queued);
        var oldest = queued.OrderBy(j => j.CreatedAt).FirstOrDefault();
        var running = db.SearchJobs.AsNoTracking()
            .Where(j => j.State == SearchJobState.Running)
            .Select(j => j.Key)
            .FirstOrDefault();
        var expired = db.SearchJobs.Count(j => j.State == SearchJobState.Expired && j.CompletedAt >= dayAgo);
        return new SearchQueueStatus(
            queued.Count(), running, oldest is null ? null : now - oldest.CreatedAt, expired, throttle.HourlyCapReached(now));
    }

    /// <summary>Called by the worker after it moves a job to a terminal state; wakes everyone waiting on it.</summary>
    internal void NotifyFinished(string key)
    {
        if (_waiters.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult();
        }
    }

    internal static string WriteRequest(SearchRequest r) => JsonSerializer.Serialize(r);

    internal static SearchRequest ReadRequest(string json) =>
        JsonSerializer.Deserialize<SearchRequest>(json) ?? throw new InvalidOperationException("Empty search request JSON.");

    /// <summary>
    /// Either hits to return now, or the key of the job to wait for plus a task that completes when it finishes.
    /// </summary>
    private async Task<(IReadOnlyList<HitEntity>? Hits, string? JobKey, Task? Completion)> DecideAsync(
        IMediaProfile p, SearchRequest r, SearchKey key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var fresh = now - _options.CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var exact = await db.SearchJobs.SingleOrDefaultAsync(j => j.Key == key.Value, ct);

            // 3. Fresh cache hit.
            if (exact is { State: SearchJobState.Done, CompletedAt: { } done } && done > fresh)
            {
                var cached = await HitsForAsync(db, exact, ct);
                logger.LogInformation("Search {Key}: {Count} release(s) cached from {Age} ago", exact.Key, cached.Count, Age(now - done));
                return (cached, null, null);
            }

            // A recent failure is not retried yet.
            if (exact is { State: SearchJobState.Failed, CompletedAt: { } failed } && failed > now - FailedRetryAfter)
            {
                logger.LogInformation("Search {Key} failed {Age} ago ({Error}); not retrying yet", exact.Key, Age(now - failed), exact.Error);
                return (await HitsForAsync(db, exact, ct), null, null);
            }

            // 4. Coalesce onto a live or fresh job whose tokens cover ours (title-only after author+title).
            var recent = await db.SearchJobs
                .Where(j => j.Profile == p.Name
                    && j.CreatedAt > fresh
                    && (j.State == SearchJobState.Queued || j.State == SearchJobState.Running || j.State == SearchJobState.Done))
                .ToListAsync(ct);
            var target = recent
                .Where(j => j.Key == key.Value || (p.CoalesceBySuperset && SearchKey.TokensOf(j.Key).IsSupersetOf(key.Tokens)))
                .OrderByDescending(j => j.Key == key.Value)
                .ThenByDescending(j => j.CreatedAt)
                .FirstOrDefault();

            if (target is { State: SearchJobState.Done })
            {
                var covered = await HitsForAsync(db, target, ct);
                logger.LogInformation("Search {Key}: {Count} release(s) cached from the broader search {Target}", key.Value, covered.Count, target.Key);
                return (covered, null, null);
            }

            if (target is not null)
            {
                logger.LogInformation("Search {Key}: waiting for the {State} search {Target}", key.Value, target.State, target.Key);
            }
            else
            {
                // 5. Upsert our own job.
                target = exact ?? db.SearchJobs.Add(new SearchJobEntity
                {
                    Key = key.Value,
                    Profile = p.Name,
                    QueryText = p.BuildQuery(r),
                    RequestJson = WriteRequest(r),
                    State = SearchJobState.Queued,
                    CreatedAt = now,
                    LastRequestedAt = now,
                }).Entity;
                if (target.State is SearchJobState.Done or SearchJobState.Failed or SearchJobState.Expired)
                {
                    Requeue(target, p, r, now);
                }

                logger.LogInformation("Queued Soulseek search \"{Query}\" ({Key})", target.QueryText, target.Key);
            }

            if (target.State == SearchJobState.Queued)
            {
                // A re-request bumps the job to the front of the LIFO queue.
                target.LastRequestedAt = now;
                await db.SaveChangesAsync(ct);
                _signal.Writer.TryWrite(target.Key);
            }

            // Register before re-reading the state: the worker writes the terminal state before notifying,
            // so either we see it below or the notification reaches this waiter.
            var completion = _waiters.GetOrAdd(target.Key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            var state = await db.SearchJobs.AsNoTracking().Where(j => j.Key == target.Key).Select(j => j.State).SingleAsync(ct);
            if (state is not (SearchJobState.Queued or SearchJobState.Running))
            {
                var finished = await db.SearchJobs.AsNoTracking().SingleAsync(j => j.Key == target.Key, ct);
                return (await HitsForAsync(db, finished, ct), null, null);
            }

            return (null, target.Key, completion);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Age(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? Invariant($"{age.TotalSeconds:0}s")
        : age < TimeSpan.FromHours(1) ? Invariant($"{age.TotalMinutes:0}m")
        : Invariant($"{age.TotalHours:0.#}h");

    private static void Requeue(SearchJobEntity job, IMediaProfile p, SearchRequest r, DateTimeOffset now)
    {
        job.State = SearchJobState.Queued;
        job.QueryText = p.BuildQuery(r);
        job.RequestJson = WriteRequest(r);
        job.CreatedAt = now;
        job.LastRequestedAt = now;
        job.CompletedAt = null;
        job.SlskdSearchId = null;
        job.Error = null;
    }

    /// <summary>
    /// A Done job's hits are those its last run saw (stored with <c>SeenAt == CompletedAt</c>); otherwise
    /// whatever hits exist for the key. Ordered by rank.
    /// </summary>
    private static async Task<IReadOnlyList<HitEntity>> HitsForAsync(SlskarrDb db, SearchJobEntity job, CancellationToken ct)
    {
        var hits = db.Hits.AsNoTracking().Where(h => h.SearchKey == job.Key);
        if (job is { State: SearchJobState.Done, CompletedAt: { } completedAt })
        {
            hits = hits.Where(h => h.SeenAt >= completedAt);
        }

        return await hits.OrderBy(h => h.Rank).ToListAsync(ct);
    }
}
