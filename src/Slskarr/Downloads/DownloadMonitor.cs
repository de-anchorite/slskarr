using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Soulseek;

namespace Slskarr.Downloads;

/// <summary>
/// Polls slskd for every active download job, mirrors its state and applies the patience rule:
/// any progress (queue position change, more bytes) resets the stall clock; no progress for
/// <see cref="DownloadOptions.StallTimeout"/> fails the job and cancels the transfer.
/// </summary>
public sealed class DownloadMonitor(
    IDbContextFactory<SlskarrDb> dbFactory,
    ISlskdClient slskd,
    IOptions<SlskarrOptions> options,
    TimeProvider time,
    ILogger<DownloadMonitor> logger)
    : BackgroundService
{
    /// <summary>
    /// How long a job may have no slskd transfer before a missing or empty batch counts against it. slskd creates
    /// the batch, connects to the peer and only then inserts the transfer, so an add or retry still in flight looks
    /// like a vanished transfer. Longer than the manager's enqueue timeout plus its follow-up probe.
    /// </summary>
    internal static readonly TimeSpan AddGrace = TimeSpan.FromMinutes(3);

    private readonly SlskarrOptions _options = options.Value;

    // When slskd first became unreachable for a job; the outage is not held against its stall clock.
    private readonly Dictionary<Guid, DateTimeOffset> _unreachableSince = [];

    // Slskarr's own downtime is not held against a job's stall clock either: until a job's first poll in this
    // process has been saved, its clock is moved up to the time this process started.
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private readonly HashSet<Guid> _resumed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                await Task.Delay(_options.Downloads.PollInterval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download monitor poll failed");
                try
                {
                    await Task.Delay(_options.Downloads.PollInterval, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One deterministic poll over all active jobs.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ids = await db.DownloadJobs.AsNoTracking()
                .Where(j => j.State == DownloadState.Queued || j.State == DownloadState.Downloading)
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .ToListAsync(ct);
        }

        foreach (var gone in _unreachableSince.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _unreachableSince.Remove(gone);
        }

        _resumed.IntersectWith(ids);

        foreach (var id in ids)
        {
            try
            {
                await PollJobAsync(id, ct);
            }
            catch (SlskdUnavailableException ex)
            {
                // Nothing was saved for this job and its stall clock did not move.
                logger.LogWarning(ex, "slskd unreachable while polling job {Job}", id);
            }
        }
    }

    private async Task PollJobAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.DownloadJobs.Include(j => j.Files).FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || job.State is not (DownloadState.Queued or DownloadState.Downloading))
        {
            return;
        }

        var now = time.GetUtcNow();
        if (!_resumed.Contains(id) && job.LastProgressAt < _startedAt)
        {
            job.LastProgressAt = _startedAt;
        }

        SlskdBatch? batch;
        try
        {
            batch = await slskd.GetBatchAsync(job.Files[0].BatchId, ct);
        }
        catch (SlskdUnavailableException)
        {
            _unreachableSince.TryAdd(id, now);
            throw;
        }

        if (_unreachableSince.Remove(id, out var since))
        {
            job.LastProgressAt = new[] { now, job.LastProgressAt + (now - since) }.Min();
        }

        var transfer = batch?.Transfers.FirstOrDefault(t => t.Filename == job.RemotePath) ?? batch?.Transfers.FirstOrDefault();
        if (transfer is null)
        {
            if (!InAddGrace(job, now))
            {
                await ResolveMissingAsync(job, now, ct);
            }
        }
        else
        {
            job.Files[0].SlskdTransferId ??= transfer.Id;
            if (SlskdStates.IsSucceeded(transfer.State))
            {
                Complete(job, now);
                job.BytesDone = transfer.BytesTransferred;
            }
            else if (SlskdStates.IsFailed(transfer.State, out var reason))
            {
                Fail(job, "Soulseek: " + reason);
            }
            else if (SlskdStates.IsInProgress(transfer.State))
            {
                job.State = DownloadState.Downloading;
                job.PlaceInQueue = null;
                if (transfer.BytesTransferred > job.BytesDone)
                {
                    job.LastProgressAt = now;
                }

                job.BytesDone = transfer.BytesTransferred;
            }
            else if (SlskdStates.IsQueued(transfer.State))
            {
                job.State = DownloadState.Queued;
                int? place;
                try
                {
                    place = await slskd.GetPlaceInQueueAsync(job.Username, transfer.Id, ct);
                }
                catch (SlskdUnavailableException)
                {
                    // slskd answered the batch call, so this is the peer being unreachable: position
                    // unknown, no progress, and the stall clock keeps running.
                    place = job.PlaceInQueue;
                }

                if (place != job.PlaceInQueue)
                {
                    job.PlaceInQueue = place;
                    job.LastProgressAt = now;
                }
            }

            if (job.State is DownloadState.Queued or DownloadState.Downloading
                && now - job.LastProgressAt > _options.Downloads.StallTimeout)
            {
                await slskd.CancelDownloadAsync(job.Username, transfer.Id, remove: true, ct);
                Fail(job, $"Stalled: no progress for {_options.Downloads.StallTimeout}");
            }
        }

        // A delete may have landed while we were talking to slskd; do not resurrect the job.
        var current = await db.DownloadJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.State).FirstAsync(ct);
        if (current is DownloadState.Queued or DownloadState.Downloading)
        {
            var file = job.Files[0];
            file.State = job.State;
            file.BytesDone = job.BytesDone;
            file.PlaceInQueue = job.PlaceInQueue;
            await db.SaveChangesAsync(ct);
        }

        _resumed.Add(id);
    }

    /// <summary>
    /// An add or retry may still be enqueueing: no transfer id yet, and the latest (re)enqueue is recent.
    /// <see cref="DownloadJobEntity.LastProgressAt"/> is that time, as both add and retry set it and nothing else
    /// moves it forward before the job has a transfer.
    /// </summary>
    private static bool InAddGrace(DownloadJobEntity job, DateTimeOffset now) =>
        job.Files[0].SlskdTransferId is null && now - job.LastProgressAt < AddGrace;

    /// <summary>slskd no longer knows the transfer: it only counts as done if the full file is on disk.</summary>
    private async Task ResolveMissingAsync(DownloadJobEntity job, DateTimeOffset now, CancellationToken ct)
    {
        var files = await slskd.ListDownloadDirAsync(StoragePaths.JobDir(job.Id), ct);
        var name = SoulseekPath.FileName(job.RemotePath);
        if (files?.Any(f => string.Equals(f.Name, name, StringComparison.Ordinal) && f.Length == job.Size) == true)
        {
            Complete(job, now);
            job.BytesDone = job.Size;
        }
        else
        {
            Fail(job, "Transfer disappeared from slskd");
        }
    }

    private void Complete(DownloadJobEntity job, DateTimeOffset now)
    {
        job.State = DownloadState.Completed;
        job.CompletedAt = now;
        job.PlaceInQueue = null;
        job.StoragePath = StoragePaths.Storage(_options.Slskd, _options.Paths, job.Id, job.RemotePath);
    }

    private static void Fail(DownloadJobEntity job, string message)
    {
        job.State = DownloadState.Failed;
        job.FailMessage = message;
        job.PlaceInQueue = null;
    }
}
