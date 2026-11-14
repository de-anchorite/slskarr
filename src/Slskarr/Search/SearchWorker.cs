using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Profiles;
using Slskarr.Soulseek;

namespace Slskarr.Search;

/// <summary>
/// Runs queued searches against slskd, newest request first (LIFO), within the throttle and the
/// configured concurrency. Also expires stale queued jobs and prunes old hits and finished searches.
/// </summary>
public sealed class SearchWorker(
    SearchCoordinator coordinator,
    SearchThrottle throttle,
    ProfileRegistry registry,
    Ranker ranker,
    ISlskdClient slskd,
    IDbContextFactory<SlskarrDb> dbFactory,
    IOptions<SlskarrOptions> options,
    TimeProvider time,
    ILogger<SearchWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private const string Disappeared = "Search disappeared from slskd";

    private readonly SearchOptions _options = options.Value.Search;
    private readonly int _concurrency = Math.Max(1, options.Value.Search.Concurrency);
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.Search.Concurrency));
    private bool _initialized;
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    /// <summary>
    /// One deterministic step: housekeeping, then claim and run the next job to completion.
    /// Returns false when no job ran (queue empty or throttled).
    /// </summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            var (job, _) = await ClaimNextAsync(ct);
            if (job is null)
            {
                return false;
            }

            await ProcessAsync(job, ct);
            return true;
        }
        finally
        {
            _slots.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(stoppingToken);
                SearchJobEntity? job;
                DateTimeOffset? notBefore;
                try
                {
                    (job, notBefore) = await ClaimNextAsync(stoppingToken);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }

                if (job is not null)
                {
                    _ = RunClaimedAsync(job, stoppingToken);
                    continue;
                }

                _slots.Release();
                await WaitForWorkAsync(notBefore, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Search worker loop failed; retrying");
                await Task.Delay(PollInterval, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        // Let in-flight jobs observe the cancellation and stop before the host disposes their services.
        for (var i = 0; i < _concurrency; i++)
        {
            await _slots.WaitAsync(CancellationToken.None);
        }
    }

    private async Task RunClaimedAsync(SearchJobEntity job, CancellationToken ct)
    {
        try
        {
            await ProcessAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Left Running; restart recovery re-queues it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search {Key} failed unexpectedly", job.Key);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Waits for a queue signal, the throttle delay, or the next prune, whichever comes first.</summary>
    private async Task WaitForWorkAsync(DateTimeOffset? notBefore, CancellationToken ct)
    {
        var wakeAt = notBefore is { } n && n < _nextPrune ? n : _nextPrune;
        var delay = wakeAt - time.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signal = coordinator.Signals.WaitToReadAsync(cts.Token).AsTask();
        var timer = Task.Delay(delay, time, cts.Token);
        await Task.WhenAny(signal, timer);
        await cts.CancelAsync();
        while (coordinator.Signals.TryRead(out _))
        {
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Housekeeping, then marks the newest-requested queued job Running. Returns the job, or null and
    /// (when throttled) the earliest time a search may start.
    /// </summary>
    private async Task<(SearchJobEntity? Job, DateTimeOffset? NotBefore)> ClaimNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        if (!_initialized)
        {
            await RecoverAsync(db, now, ct);
            _initialized = true;
        }

        if (now >= _nextPrune)
        {
            await PruneAsync(db, now, ct);
            _nextPrune = now + PruneInterval;
        }

        await ExpireAsync(db, now, ct);

        var job = await db.SearchJobs
            .Where(j => j.State == SearchJobState.Queued)
            .OrderByDescending(j => j.LastRequestedAt)
            .FirstOrDefaultAsync(ct);
        if (job is null)
        {
            return (null, null);
        }

        if (throttle.DelayBeforeNext(now) is { } delay)
        {
            return (null, now + delay);
        }

        job.State = SearchJobState.Running;
        await db.SaveChangesAsync(ct);
        throttle.Record(now);
        return (job, null);
    }

    /// <summary>Restart recovery: interrupted jobs go back to the queue; recent searches seed the throttle.</summary>
    private async Task RecoverAsync(SlskarrDb db, DateTimeOffset now, CancellationToken ct)
    {
        var interrupted = await db.SearchJobs
            .Where(j => j.State == SearchJobState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, SearchJobState.Queued), ct);
        if (interrupted > 0)
        {
            logger.LogInformation("Re-queued {Count} search(es) interrupted by a restart", interrupted);
        }

        var hourAgo = now - TimeSpan.FromHours(1);
        var recent = await db.SearchJobs.AsNoTracking()
            .Where(j => (j.State == SearchJobState.Done || j.State == SearchJobState.Failed) && j.CompletedAt > hourAgo)
            .Select(j => j.CompletedAt)
            .ToListAsync(ct);
        foreach (var completedAt in recent)
        {
            throttle.Record(completedAt!.Value);
        }
    }

    private async Task ExpireAsync(SlskarrDb db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - _options.QueuedExpiry;
        var expired = await db.SearchJobs
            .Where(j => j.State == SearchJobState.Queued && j.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (expired.Count == 0)
        {
            return;
        }

        foreach (var job in expired)
        {
            job.State = SearchJobState.Expired;
            job.CompletedAt = now;
        }

        await db.SaveChangesAsync(ct);
        foreach (var job in expired)
        {
            logger.LogInformation("Search {Key} expired after waiting {Expiry} in the queue", job.Key, _options.QueuedExpiry);
            coordinator.NotifyFinished(job.Key);
        }
    }

    /// <summary>
    /// Deletes hits not seen within the retention period, unless a live download refers to them, and finished
    /// searches that ended (or, lacking an end, started) before it.
    /// </summary>
    private async Task PruneAsync(SlskarrDb db, DateTimeOffset now, CancellationToken ct)
    {
        await PruneHitsAsync(db, now, ct);

        var cutoff = now - _options.Retention;
        var searches = await db.SearchJobs
            .Where(j => (j.State == SearchJobState.Done || j.State == SearchJobState.Failed || j.State == SearchJobState.Expired)
                && (j.CompletedAt < cutoff || (j.CompletedAt == null && j.CreatedAt < cutoff)))
            .ExecuteDeleteAsync(ct);
        if (searches > 0)
        {
            logger.LogInformation("Pruned {Count} finished search(es) older than {Retention}", searches, _options.Retention);
        }
    }

    private async Task PruneHitsAsync(SlskarrDb db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - _options.Retention;
        var pruned = await db.Hits
            .Where(h => h.SeenAt < cutoff
                && !db.DownloadJobs.Any(d => d.HitId == h.Id && d.State != DownloadState.Deleted))
            .ExecuteDeleteAsync(ct);
        if (pruned > 0)
        {
            logger.LogInformation("Pruned {Count} stored hit(s) older than {Retention}", pruned, _options.Retention);
        }
    }

    private async Task ProcessAsync(SearchJobEntity job, CancellationToken ct)
    {
        try
        {
            var profile = registry.ByName(job.Profile)
                ?? throw new InvalidOperationException($"Unknown profile '{job.Profile}'.");
            var request = SearchCoordinator.ReadRequest(job.RequestJson);
            if (await SearchAsync(job, profile, request, ct) is { } failure)
            {
                await FailAsync(job.Key, failure, ct);
            }
        }
        catch (SlskdUnavailableException ex)
        {
            logger.LogWarning("Search {Key} failed: slskd unavailable: {Error}", job.Key, ex.Message);
            await FailAsync(job.Key, ex.Message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Search {Key} failed", job.Key);
            await FailAsync(job.Key, ex.Message, ct);
        }
    }

    /// <summary>Runs the slskd search and completes the job; returns the failure message when it can't.</summary>
    private async Task<string?> SearchAsync(
        SearchJobEntity job, IMediaProfile profile, SearchRequest request, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var id = await slskd.StartSearchAsync(job.QueryText, _options.SlskdSearchTimeoutSeconds, _options.ResponseLimit, ct);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.SearchJobs.Where(j => j.Key == job.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.SlskdSearchId, id), ct);
        }

        var deadline = started + TimeSpan.FromSeconds(_options.SlskdSearchTimeoutSeconds + 10);
        while (true)
        {
            var progress = await slskd.GetSearchAsync(id, includeResponses: false, ct);
            if (progress is null)
            {
                return Disappeared;
            }

            if (progress.IsComplete)
            {
                break;
            }

            if (time.GetUtcNow() >= deadline)
            {
                logger.LogWarning("Search {Key} still not complete after {Seconds}s; taking the responses so far",
                    job.Key, (deadline - started).TotalSeconds);
                break;
            }

            await Task.Delay(PollInterval, time, ct);
        }

        var search = await slskd.GetSearchAsync(id, includeResponses: true, ct);
        if (search is null)
        {
            return Disappeared;
        }

        var ranked = ranker.Rank(profile.ToCandidates(search.Responses ?? []), request, profile, profile.Ranking, _options.ResultCap);
        await CompleteAsync(job.Key, ranked, ct);

        try
        {
            await slskd.DeleteSearchAsync(id, ct);
        }
        catch (SlskdUnavailableException ex)
        {
            logger.LogWarning("Could not delete slskd search {Id}: {Error}", id, ex.Message);
        }

        return null;
    }

    /// <summary>
    /// Stores the hits and marks the job Done in one transaction. Hits upsert by (SearchKey, Username,
    /// RemotePath), so existing ones keep their Id. Hits get <c>SeenAt</c> equal to the job's <c>CompletedAt</c>,
    /// which is how the coordinator tells this run's hits from older ones.
    /// </summary>
    private async Task CompleteAsync(string key, IReadOnlyList<ReleaseCandidate> ranked, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var now = time.GetUtcNow();
            var existing = (await db.Hits.Where(h => h.SearchKey == key).ToListAsync(ct))
                .ToDictionary(h => (h.Username, h.RemotePath));
            for (var i = 0; i < ranked.Count; i++)
            {
                var c = ranked[i];
                if (!existing.TryGetValue((c.Username, c.RemotePath), out var hit))
                {
                    hit = db.Hits.Add(new HitEntity
                    {
                        Id = NewHitId(),
                        SearchKey = key,
                        Profile = c.Profile,
                        Username = c.Username,
                        RemotePath = c.RemotePath,
                        ReleaseTitle = c.ReleaseTitle,
                        Format = c.Name.Format,
                    }).Entity;
                }

                hit.Size = c.Size;
                hit.ReleaseTitle = c.ReleaseTitle;
                hit.ParsedAuthor = c.Name.Author;
                hit.ParsedTitle = c.Name.Title;
                hit.Year = c.Name.Year;
                hit.Format = c.Name.Format;
                hit.FreeSlot = c.FreeSlot;
                hit.QueueLength = c.QueueLength;
                hit.UploadSpeed = c.UploadSpeed;
                hit.Rank = i + 1;
                hit.SeenAt = now;
            }

            var job = await db.SearchJobs.SingleAsync(j => j.Key == key, ct);
            job.State = SearchJobState.Done;
            job.CompletedAt = now;
            job.Error = null;
            await db.SaveChangesAsync(ct);
        }

        coordinator.NotifyFinished(key);
    }

    private async Task FailAsync(string key, string error, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.SearchJobs.Where(j => j.Key == key).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, SearchJobState.Failed)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.CompletedAt, now), ct);
        }

        coordinator.NotifyFinished(key);
    }

    private static string NewHitId() => System.Buffers.Text.Base64Url.EncodeToString(Guid.NewGuid().ToByteArray());
}
