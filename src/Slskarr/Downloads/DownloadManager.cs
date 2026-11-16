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

    /// <summary>
    /// Upper bound for one batch enqueue. The enqueue does not follow the caller's token: a disconnecting *arr
    /// must not split an enqueue that slskd may already be acting on.
    /// </summary>
    internal static readonly TimeSpan EnqueueTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound for the batch lookup after an enqueue broke off.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

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

            // From here on the job exists; the caller's token no longer applies.
            try
            {
                await EnqueueAsync(job);
            }
            catch
            {
                // slskd has no batch for it (or could not say): leave no half-created job so the *arr's retry starts clean.
                db.DownloadJobs.Remove(job);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            await db.SaveChangesAsync(CancellationToken.None);
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
                var result = wasState == DownloadState.Completed
                    ? await slskd.DeleteDownloadDirAsync(StoragePaths.JobDir(job.Id), ct)
                    : await DeletePartialFileAsync(db, job, ct);
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

    /// <summary>
    /// Deletes the job's partial file from slskd's incomplete directory. That directory is shared by everything
    /// from the same peer folder, so only the one file goes, and not while another live job downloads the same file.
    /// </summary>
    private async Task<SlskdDeleteResult?> DeletePartialFileAsync(SlskarrDb db, DownloadJobEntity job, CancellationToken ct)
    {
        if (StoragePaths.IncompleteFile(job.Username, job.RemotePath) is not { } file)
        {
            return null;
        }

        var shared = await db.DownloadJobs.AnyAsync(
            j => j.Id != job.Id && j.State != DownloadState.Deleted && j.Username == job.Username && j.RemotePath == job.RemotePath,
            ct);
        if (shared)
        {
            logger.LogInformation("Keeping the partial file of {Job}: another download uses it", job.Id);
            return null;
        }

        return await slskd.DeleteIncompleteFileAsync(file, ct);
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

            // Throws, saving nothing (the job stays Failed), only when slskd has no batch for the retry.
            await EnqueueAsync(job);
            await db.SaveChangesAsync(CancellationToken.None);
            return new AddResult(true, NzoId(job.Id), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Enqueues the job's batch and records the outcome on the (tracked) job; the caller saves. When the enqueue
    /// breaks off (slskd unreachable or too slow), slskd may still have created the batch, and a second enqueue
    /// would be refused as already in progress, so the batch is looked up: if slskd has it, the job stays Queued
    /// (with no transfer yet if the batch is still empty, which the monitor's add grace settles). Throws
    /// <see cref="SlskdUnavailableException"/> only when slskd has no such batch or cannot be asked.
    /// </summary>
    private async Task EnqueueAsync(DownloadJobEntity job)
    {
        SlskdEnqueueResult result;
        using (var timeout = new CancellationTokenSource(EnqueueTimeout, time))
        {
            try
            {
                result = await slskd.EnqueueBatchAsync(
                    job.BatchId, job.Username, [new SlskdEnqueueFile(job.RemotePath, job.Size)], StoragePaths.JobDir(job.Id), timeout.Token);
            }
            catch (Exception ex) when (ex is SlskdUnavailableException
                || (ex is OperationCanceledException && timeout.IsCancellationRequested))
            {
                var unavailable = ex as SlskdUnavailableException
                    ?? new SlskdUnavailableException($"slskd did not answer the enqueue within {EnqueueTimeout}", ex);
                if (!await AdoptBatchAsync(job))
                {
                    throw unavailable;
                }

                return;
            }
        }

        if (!result.Ok)
        {
            job.State = DownloadState.Failed;
            job.FailMessage = result.Error ?? "Enqueue failed";
            return;
        }

        job.SlskdTransferId = result.Batch?.Transfers.FirstOrDefault()?.Id;
    }

    /// <summary>After a broken-off enqueue: true (transfer id recorded, if any) when slskd has the job's batch.</summary>
    private async Task<bool> AdoptBatchAsync(DownloadJobEntity job)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, time);
        try
        {
            var batch = await slskd.GetBatchAsync(job.BatchId, timeout.Token);
            if (batch is null)
            {
                return false;
            }

            job.SlskdTransferId = batch.Transfers.FirstOrDefault()?.Id;
            logger.LogInformation("Enqueue for {Job} broke off but slskd has its batch ({Count} transfer(s)); keeping it",
                job.Id, batch.Transfers.Count);
            return true;
        }
        catch (Exception ex) when (ex is SlskdUnavailableException
            || (ex is OperationCanceledException && timeout.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not look up the batch of {Job} after a failed enqueue", job.Id);
            return false;
        }
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
