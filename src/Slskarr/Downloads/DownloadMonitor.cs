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
    private readonly SlskarrOptions _options = options.Value;

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
        var job = await db.DownloadJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || job.State is not (DownloadState.Queued or DownloadState.Downloading))
        {
            return;
        }

        var now = time.GetUtcNow();
        var batch = await slskd.GetBatchAsync(job.BatchId, ct);
        var transfer = batch?.Transfers.FirstOrDefault(t => t.Filename == job.RemotePath) ?? batch?.Transfers.FirstOrDefault();
        if (transfer is null)
        {
            await ResolveMissingAsync(job, now, ct);
        }
        else
        {
            job.SlskdTransferId ??= transfer.Id;
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
                var place = await slskd.GetPlaceInQueueAsync(job.Username, transfer.Id, ct);
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
            await db.SaveChangesAsync(ct);
        }
    }

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
