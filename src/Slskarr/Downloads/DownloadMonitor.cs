using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Soulseek;

namespace Slskarr.Downloads;

/// <summary>
/// Polls slskd for every active download job, mirrors its state and applies the patience rule:
/// any progress (queue position change, more bytes) resets the stall clock; no progress for
/// <see cref="DownloadOptions.StallTimeout"/> fails the job and cancels its transfers. A job is one or more files
/// in one or more slskd batches; its state is aggregated over all files, and one failed file fails the job.
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

    // Slskarr's own downtime is not held against a job's stall clock (or its add grace) either: until a job's first
    // poll in this process has been saved, both are moved up to the time this process started.
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
        if (!_resumed.Contains(id))
        {
            if (job.LastProgressAt < _startedAt)
            {
                job.LastProgressAt = _startedAt;
            }

            // An add or retry in flight when Slskarr stopped gets its full add grace again.
            if (job.EnqueuedAt < _startedAt)
            {
                job.EnqueuedAt = _startedAt;
            }
        }

        // One poll per batch (disc folder); a file is matched to its transfer by remote path.
        var transfers = new Dictionary<JobFileEntity, SlskdTransfer?>();
        foreach (var group in job.Files.GroupBy(f => f.BatchId))
        {
            SlskdBatch? batch;
            try
            {
                batch = await slskd.GetBatchAsync(group.Key, ct);
            }
            catch (SlskdUnavailableException)
            {
                _unreachableSince.TryAdd(id, now);
                throw;
            }

            foreach (var file in group)
            {
                // A single-file job also accepts its batch's only transfer under another name (ebooks, as before).
                transfers[file] = batch?.Transfers.FirstOrDefault(t => t.Filename == file.RemotePath)
                    ?? (job.Files.Count == 1 ? batch?.Transfers.FirstOrDefault() : null);
            }
        }

        if (_unreachableSince.Remove(id, out var since))
        {
            job.LastProgressAt = new[] { now, job.LastProgressAt + (now - since) }.Min();
        }

        string? failure = null;
        var gone = new List<JobFileEntity>();
        var finished = new List<JobFileEntity>(); // failed in slskd already: nothing left to cancel
        var queued = new List<(JobFileEntity File, SlskdTransfer Transfer)>();
        foreach (var file in job.Files)
        {
            var transfer = transfers[file];
            if (transfer is null)
            {
                if (!InAddGrace(file, job, now))
                {
                    gone.Add(file);
                }

                continue;
            }

            file.SlskdTransferId ??= transfer.Id;
            if (SlskdStates.IsSucceeded(transfer.State))
            {
                Mirror(job, file, DownloadState.Completed, transfer.BytesTransferred, null, now);
            }
            else if (SlskdStates.IsFailed(transfer.State, out var reason))
            {
                failure ??= FailureMessage(job, file, reason);
                finished.Add(file);
            }
            else if (SlskdStates.IsInProgress(transfer.State))
            {
                Mirror(job, file, DownloadState.Downloading, transfer.BytesTransferred, null, now);
            }
            else if (SlskdStates.IsQueued(transfer.State))
            {
                queued.Add((file, transfer));
            }
        }

        // One queue-position request per batch per poll (the first queued file by remote path): a peer request
        // per track would be 12 per album every poll. The others keep their last known position.
        foreach (var batch in queued.GroupBy(q => q.File.BatchId))
        {
            var asked = batch.MinBy(q => q.File.RemotePath, StringComparer.Ordinal);
            foreach (var (file, transfer) in batch)
            {
                var place = file.PlaceInQueue;
                if (file == asked.File)
                {
                    try
                    {
                        place = await slskd.GetPlaceInQueueAsync(job.Username, transfer.Id, ct);
                    }
                    catch (SlskdUnavailableException)
                    {
                        // slskd answered the batch call, so this is the peer being unreachable: position
                        // unknown, no progress, and the stall clock keeps running.
                    }
                }

                Mirror(job, file, DownloadState.Queued, transfer.BytesTransferred, place, now);
            }
        }

        if (failure is null && gone.Count > 0 && !await ResolveMissingAsync(job, gone, now, ct))
        {
            failure = "Transfer disappeared from slskd";
        }

        if (failure is not null)
        {
            if (!await FailAsync(job, failure, finished, ct))
            {
                return; // removal not confirmed: the job stays active, nothing is saved, the next poll retries
            }
        }
        else if (job.Files.All(f => f.State == DownloadState.Completed))
        {
            job.State = DownloadState.Completed;
            job.CompletedAt = now;
            job.StoragePath = StoragePaths.Storage(_options.Slskd, _options.Paths, job.Id, IsSingleFileRelease(job) ? job.RemotePath : null);
        }
        else
        {
            job.State = job.Files.Any(f => f.State == DownloadState.Downloading) ? DownloadState.Downloading : DownloadState.Queued;
            if (now - job.LastProgressAt > _options.Downloads.StallTimeout
                && !await FailAsync(job, $"Stalled: no progress for {_options.Downloads.StallTimeout}", [], ct))
            {
                return;
            }
        }

        job.BytesDone = job.Files.Sum(f => f.BytesDone);
        job.PlaceInQueue = job.State is DownloadState.Queued or DownloadState.Downloading
            ? job.Files.Where(f => f.State == DownloadState.Queued && f.PlaceInQueue is not null).Min(f => f.PlaceInQueue)
            : null;

        // A delete may have landed while we were talking to slskd; do not resurrect the job.
        var current = await db.DownloadJobs.AsNoTracking().Where(j => j.Id == id).Select(j => j.State).FirstAsync(ct);
        if (current is DownloadState.Queued or DownloadState.Downloading)
        {
            await db.SaveChangesAsync(ct);
        }

        _resumed.Add(id);
    }

    /// <summary>Records a file's state; more bytes or a moved queue position count as progress.</summary>
    private static void Mirror(DownloadJobEntity job, JobFileEntity file, DownloadState state, long bytes, int? place, DateTimeOffset now)
    {
        if (bytes > file.BytesDone || (state == DownloadState.Queued && place != file.PlaceInQueue))
        {
            job.LastProgressAt = now;
        }

        file.State = state;
        file.BytesDone = bytes;
        file.PlaceInQueue = place;
    }

    private static string FailureMessage(DownloadJobEntity job, JobFileEntity file, string reason) =>
        job.Files.Count > 1 ? $"Soulseek: {reason} ({SoulseekPath.FileName(file.RemotePath)})" : "Soulseek: " + reason;

    /// <summary>Cancels and removes every transfer of the job (but <paramref name="except"/>), then marks the job and all its files failed.</summary>
    private async Task<bool> FailAsync(DownloadJobEntity job, string message, List<JobFileEntity> except, CancellationToken ct)
    {
        if (!await TransferCleanup.RemoveAsync(slskd, logger, job, [.. job.Files.Except(except)], ct))
        {
            return false;
        }

        job.State = DownloadState.Failed;
        job.FailMessage = message;
        foreach (var file in job.Files)
        {
            file.State = DownloadState.Failed;
        }

        return true;
    }

    /// <summary>
    /// An ebook-shaped job (one file that is the release itself) is stored as that file; anything else (an album,
    /// even one with a single track) as the job folder. Decided by the job, not its SAB category, which can change.
    /// </summary>
    private static bool IsSingleFileRelease(DownloadJobEntity job) =>
        job.Files.Count == 1 && job.Files[0].RemotePath == job.RemotePath;

    /// <summary>
    /// A file with no recorded transfer may still be enqueueing: it has no transfer id and the job's latest
    /// (re)enqueue, <see cref="DownloadJobEntity.EnqueuedAt"/> (set by add and retry), is recent. Progress on
    /// other files does not move it, so a batch that never appears is detected AddGrace after the enqueue.
    /// </summary>
    private static bool InAddGrace(JobFileEntity file, DownloadJobEntity job, DateTimeOffset now) =>
        file.SlskdTransferId is null && now - job.EnqueuedAt < AddGrace;

    /// <summary>
    /// slskd no longer knows these files' transfers: each only counts as done if its full file is on disk
    /// (one listing per disc folder). Returns false when any is not.
    /// </summary>
    private async Task<bool> ResolveMissingAsync(DownloadJobEntity job, List<JobFileEntity> gone, DateTimeOffset now, CancellationToken ct)
    {
        var ok = true;
        foreach (var dir in gone.GroupBy(f => f.Subdir))
        {
            var listing = await slskd.ListDownloadDirAsync(StoragePaths.JobDir(job.Id, dir.Key), ct);
            foreach (var file in dir)
            {
                var name = SoulseekPath.FileName(file.RemotePath);
                if (listing?.Any(l => string.Equals(l.Name, name, StringComparison.Ordinal) && l.Length == file.Size) == true)
                {
                    Mirror(job, file, DownloadState.Completed, file.Size, null, now);
                }
                else
                {
                    ok = false;
                }
            }
        }

        return ok;
    }
}
