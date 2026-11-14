using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Soulseek;

namespace Slskarr.Downloads;

public sealed record AddResult(bool Ok, string? NzoId, string? Error);

/// <summary>Creates, lists, deletes and retries download jobs (the SABnzbd-facing operations).</summary>
public sealed class DownloadManager(
    IDbContextFactory<SlskarrDb> dbFactory,
    ISlskdClient slskd,
    TimeProvider time,
    FileManagementStatus status,
    ILogger<DownloadManager> logger)
{
    private const string NzoPrefix = "SLSKARR_";

    // Serialises Add/Retry so concurrent duplicates yield one job and one enqueue.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string NzoId(Guid id) => NzoPrefix + id.ToString("N");

    public static Guid? ParseNzoId(string nzoId) =>
        nzoId.StartsWith(NzoPrefix, StringComparison.Ordinal) && Guid.TryParseExact(nzoId[NzoPrefix.Length..], "N", out var id)
            ? id
            : null;

    public async Task<AddResult> AddAsync(string hitId, string category, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var existing = await db.DownloadJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.HitId == hitId && j.State != DownloadState.Deleted, ct);
            if (existing is not null)
            {
                return new AddResult(true, NzoId(existing.Id), null);
            }

            var hit = await db.Hits.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hitId, ct);
            if (hit is null)
            {
                return new AddResult(false, null, "Release expired or unknown");
            }

            var now = time.GetUtcNow();
            var id = Guid.NewGuid();
            var job = new DownloadJobEntity
            {
                Id = id,
                BatchId = id,
                HitId = hit.Id,
                Category = category,
                Title = hit.ReleaseTitle,
                Username = hit.Username,
                RemotePath = hit.RemotePath,
                Size = hit.Size,
                State = DownloadState.Queued,
                LastProgressAt = now,
                CreatedAt = now,
            };
            db.DownloadJobs.Add(job);
            await db.SaveChangesAsync(ct);

            try
            {
                await EnqueueAsync(job, ct);
            }
            catch
            {
                // slskd unreachable: leave no half-created job so the *arr's retry starts clean.
                db.DownloadJobs.Remove(job);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            await db.SaveChangesAsync(ct);
            return new AddResult(true, NzoId(job.Id), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DownloadJobEntity>> ActiveAsync(string? category, CancellationToken ct) =>
        await ListAsync(category, [DownloadState.Queued, DownloadState.Downloading], ct);

    public async Task<IReadOnlyList<DownloadJobEntity>> HistoryAsync(string? category, CancellationToken ct) =>
        await ListAsync(category, [DownloadState.Completed, DownloadState.Failed], ct);

    public async Task<bool> DeleteAsync(string nzoId, bool deleteFiles, CancellationToken ct)
    {
        if (ParseNzoId(nzoId) is not { } id)
        {
            return false;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.DownloadJobs.FirstOrDefaultAsync(j => j.Id == id && j.State != DownloadState.Deleted, ct);
        if (job is null)
        {
            return false;
        }

        var wasState = job.State;
        await RemoveTransferAsync(job, ct);
        job.State = DownloadState.Deleted;
        await db.SaveChangesAsync(ct);

        if (deleteFiles)
        {
            try
            {
                var dir = StoragePaths.JobDir(job.Id);
                var result = wasState == DownloadState.Completed
                    ? await slskd.DeleteDownloadDirAsync(dir, ct)
                    : await slskd.DeleteIncompleteDirAsync(dir, ct);
                if (result == SlskdDeleteResult.Forbidden && status.MarkRemoteDeleteForbidden())
                {
                    logger.LogWarning("Enable remote_file_management in slskd to let Slskarr delete imported files");
                }
            }
            catch (SlskdUnavailableException ex)
            {
                logger.LogWarning(ex, "slskd unreachable while deleting files for {Job}", job.Id);
            }
        }

        return true;
    }

    public async Task<AddResult> RetryAsync(string nzoId, CancellationToken ct)
    {
        if (ParseNzoId(nzoId) is not { } id)
        {
            return new AddResult(false, null, "Unknown download");
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var job = await db.DownloadJobs.FirstOrDefaultAsync(j => j.Id == id && j.State != DownloadState.Deleted, ct);
            if (job is null)
            {
                return new AddResult(false, null, "Unknown download");
            }

            if (job.State != DownloadState.Failed)
            {
                return new AddResult(false, nzoId, "Only failed downloads can be retried");
            }

            await RemoveTransferAsync(job, ct);
            job.BatchId = Guid.NewGuid();
            job.State = DownloadState.Queued;
            job.BytesDone = 0;
            job.PlaceInQueue = null;
            job.LastProgressAt = time.GetUtcNow();
            job.CompletedAt = null;
            job.FailMessage = null;
            job.SlskdTransferId = null;
            job.StoragePath = null;
            await EnqueueAsync(job, ct);
            await db.SaveChangesAsync(ct);
            return new AddResult(true, NzoId(job.Id), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Enqueues the job's batch and records the outcome on the (tracked) job; the caller saves.</summary>
    private async Task EnqueueAsync(DownloadJobEntity job, CancellationToken ct)
    {
        var result = await slskd.EnqueueBatchAsync(
            job.BatchId, job.Username, job.RemotePath, job.Size, StoragePaths.JobDir(job.Id), ct);
        if (!result.Ok)
        {
            job.State = DownloadState.Failed;
            job.FailMessage = result.Error ?? "Enqueue failed";
            return;
        }

        job.SlskdTransferId = result.Batch?.Transfers.FirstOrDefault()?.Id;
    }

    /// <summary>Cancels and clears the job's slskd transfer record (404 is success). Never throws for slskd outages.</summary>
    private async Task RemoveTransferAsync(DownloadJobEntity job, CancellationToken ct)
    {
        try
        {
            var transferId = job.SlskdTransferId;
            if (transferId is null && job.State is DownloadState.Queued or DownloadState.Downloading)
            {
                var batch = await slskd.GetBatchAsync(job.BatchId, ct);
                transferId = batch?.Transfers.FirstOrDefault()?.Id;
            }

            if (transferId is { } tid)
            {
                await slskd.CancelDownloadAsync(job.Username, tid, remove: true, ct);
            }
        }
        catch (SlskdUnavailableException ex)
        {
            logger.LogWarning(ex, "slskd unreachable while removing transfer for {Job}", job.Id);
        }

        job.SlskdTransferId = null;
    }

    private async Task<IReadOnlyList<DownloadJobEntity>> ListAsync(string? category, DownloadState[] states, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.DownloadJobs.AsNoTracking().Where(j => states.Contains(j.State));
        if (category is not null)
        {
            query = query.Where(j => j.Category == category);
        }

        return await query.OrderBy(j => j.CreatedAt).ToListAsync(ct);
    }
}
