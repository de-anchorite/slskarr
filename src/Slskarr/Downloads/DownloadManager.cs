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

            var hit = await db.Hits.AsNoTracking().Include(h => h.Files).FirstOrDefaultAsync(h => h.Id == hitId, ct);
            if (hit is null)
            {
                return new AddResult(false, null, "Release expired or unknown");
            }

            if (hit.Files.Count == 0)
            {
                return new AddResult(false, null, "Release has no files");
            }

            var now = time.GetUtcNow();
            var id = Guid.NewGuid();
            var batches = AssignBatches(hit.Files.Select(f => f.Subdir), id);
            var job = new DownloadJobEntity
            {
                Id = id,
                HitId = hit.Id,
                Category = category,
                Title = hit.ReleaseTitle,
                Username = hit.Username,
                RemotePath = hit.RemotePath,
                Size = hit.Size,
                Files = hit.Files.Select(f => new JobFileEntity
                {
                    RemotePath = f.RemotePath, Size = f.Size, Subdir = f.Subdir, BatchId = batches[f.Subdir], State = DownloadState.Queued,
                }).ToList(),
                State = DownloadState.Queued,
                LastProgressAt = now,
                EnqueuedAt = now,
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
        var job = await db.DownloadJobs.Include(j => j.Files).FirstOrDefaultAsync(j => j.Id == id && j.State != DownloadState.Deleted, ct);
        if (job is null)
        {
            return false;
        }

        var wasState = job.State;
        await RemoveTransfersAsync(job, job.Files, ct);
        job.State = DownloadState.Deleted;
        await db.SaveChangesAsync(ct);

        if (deleteFiles)
        {
            try
            {
                // An unfinished album may already hold finished tracks in the job folder: it goes too.
                var partials = wasState == DownloadState.Completed ? null : await DeletePartialFilesAsync(db, job, ct);
                var dir = await slskd.DeleteDownloadDirAsync(StoragePaths.JobDir(job.Id), ct);
                var result = partials is { } p && p != SlskdDeleteResult.Deleted ? p : dir;
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
    /// Deletes the partial file of every unfinished file of the job from slskd's incomplete directory. That
    /// directory is shared by everything from the same peer folder, so only the job's own files go, and not
    /// while another live job downloads the same file. Returns the first non-success result.
    /// </summary>
    private async Task<SlskdDeleteResult?> DeletePartialFilesAsync(SlskarrDb db, DownloadJobEntity job, CancellationToken ct)
    {
        SlskdDeleteResult? outcome = null;
        foreach (var jobFile in job.Files.Where(f => f.State != DownloadState.Completed))
        {
            if (StoragePaths.IncompleteFile(job.Username, jobFile.RemotePath) is not { } file)
            {
                continue;
            }

            var remotePath = jobFile.RemotePath;
            var shared = await db.DownloadJobs.AnyAsync(
                j => j.Id != job.Id && j.State != DownloadState.Deleted && j.Username == job.Username
                    && j.Files.Any(f => f.RemotePath == remotePath),
                ct);
            if (shared)
            {
                logger.LogInformation("Keeping the partial file {File} of {Job}: another download uses it", file, job.Id);
                continue;
            }

            var result = await slskd.DeleteIncompleteFileAsync(file, ct);
            if (result != SlskdDeleteResult.Deleted)
            {
                outcome ??= result;
            }
        }

        return outcome;
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
            var job = await db.DownloadJobs.Include(j => j.Files).FirstOrDefaultAsync(j => j.Id == id && j.State != DownloadState.Deleted, ct);
            if (job is null)
            {
                return new AddResult(false, null, "Unknown download");
            }

            if (job.State != DownloadState.Failed)
            {
                return new AddResult(false, nzoId, "Only failed downloads can be retried");
            }

            if (job.Files.Count == 0)
            {
                return new AddResult(false, nzoId, "Download has no files");
            }

            // Nothing is saved on this path, so a failed removal leaves the job and its retained ids as they were.
            if (!await RemoveTransfersAsync(job, job.Files, ct))
            {
                return new AddResult(false, nzoId, "slskd unavailable: could not remove the old transfers; try again later");
            }

            var batches = AssignBatches(job.Files.Select(f => f.Subdir), Guid.NewGuid());
            job.State = DownloadState.Queued;
            job.BytesDone = 0;
            job.PlaceInQueue = null;
            job.LastProgressAt = time.GetUtcNow();
            job.EnqueuedAt = job.LastProgressAt;
            job.CompletedAt = null;
            job.FailMessage = null;
            job.StoragePath = null;
            foreach (var file in job.Files)
            {
                file.BatchId = batches[file.Subdir];
                file.SlskdTransferId = null;
                file.State = DownloadState.Queued;
                file.BytesDone = 0;
                file.PlaceInQueue = null;
            }

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

    /// <summary>One batch id per distinct subdir (one slskd batch per disc folder); the first takes <paramref name="first"/>.</summary>
    private static Dictionary<string, Guid> AssignBatches(IEnumerable<string> subdirs, Guid first)
    {
        var batches = new Dictionary<string, Guid>();
        foreach (var subdir in subdirs)
        {
            batches.TryAdd(subdir, batches.Count == 0 ? first : Guid.NewGuid());
        }

        return batches;
    }

    /// <summary>
    /// Enqueues the job's batches one by one and records the outcome on the (tracked) job; the caller saves. A
    /// rejected batch fails the whole job (job and file rows) after the batches enqueued before it are cancelled.
    /// Throws <see cref="SlskdUnavailableException"/> (after the same clean-up) only when slskd has no such batch
    /// or cannot be asked; see <see cref="EnqueueBatchAsync"/>.
    /// </summary>
    private async Task EnqueueAsync(DownloadJobEntity job)
    {
        var enqueued = new List<JobFileEntity>();
        foreach (var batch in job.Files.GroupBy(f => f.BatchId).Select(g => g.ToList()))
        {
            string? error;
            try
            {
                error = await EnqueueBatchAsync(job, batch);
            }
            catch (Exception ex)
            {
                if (await RemoveTransfersAsync(job, enqueued, CancellationToken.None))
                {
                    throw;
                }

                if (ex is OperationCanceledException)
                {
                    throw;
                }

                // Earlier batches may still be queued in slskd: keep the job (Failed, transfer ids kept) so a
                // later Delete/Retry can clean them up, rather than orphaning them.
                logger.LogWarning(ex, "Enqueue of a later batch failed for {Job} and the earlier batches could not be removed", job.Id);
                Fail(job, "slskd became unavailable while enqueuing; earlier parts of the release may still be queued in slskd");
                return;
            }

            if (error is not null)
            {
                await RemoveTransfersAsync(job, enqueued, CancellationToken.None);
                Fail(job, error);
                return;
            }

            enqueued.AddRange(batch);
        }
    }

    private static void Fail(DownloadJobEntity job, string message)
    {
        job.State = DownloadState.Failed;
        job.FailMessage = message;
        foreach (var file in job.Files)
        {
            file.State = DownloadState.Failed;
        }
    }

    /// <summary>
    /// Enqueues one batch (one disc folder) and records the transfer ids on its files. Returns slskd's error, or
    /// null. When the enqueue breaks off (slskd unreachable or too slow), slskd may still have created the batch,
    /// and a second enqueue would be refused as already in progress, so the batch is looked up: if slskd has it,
    /// the files stay Queued (with no transfer yet if the batch is still empty, which the monitor's add grace
    /// settles). Throws <see cref="SlskdUnavailableException"/> only when slskd has no such batch or cannot be asked.
    /// </summary>
    private async Task<string?> EnqueueBatchAsync(DownloadJobEntity job, List<JobFileEntity> batch)
    {
        SlskdEnqueueResult result;
        using (var timeout = new CancellationTokenSource(EnqueueTimeout, time))
        {
            try
            {
                result = await slskd.EnqueueBatchAsync(
                    batch[0].BatchId, job.Username, [.. batch.Select(f => new SlskdEnqueueFile(f.RemotePath, f.Size))],
                    StoragePaths.JobDir(job.Id, batch[0].Subdir), timeout.Token);
            }
            catch (Exception ex) when (ex is SlskdUnavailableException
                || (ex is OperationCanceledException && timeout.IsCancellationRequested))
            {
                var unavailable = ex as SlskdUnavailableException
                    ?? new SlskdUnavailableException($"slskd did not answer the enqueue within {EnqueueTimeout}", ex);
                if (!await AdoptBatchAsync(job, batch))
                {
                    throw unavailable;
                }

                return null;
            }
        }

        if (!result.Ok)
        {
            return result.Error ?? "Enqueue failed";
        }

        AssignTransfers(batch, result.Batch);
        return null;
    }

    /// <summary>Records each file's transfer id, matched by file name (a lone file takes a lone transfer).</summary>
    private static void AssignTransfers(List<JobFileEntity> batch, SlskdBatch? slskdBatch)
    {
        var transfers = slskdBatch?.Transfers ?? [];
        foreach (var file in batch)
        {
            file.SlskdTransferId = (transfers.FirstOrDefault(t => t.Filename == file.RemotePath)
                ?? (batch.Count == 1 ? transfers.FirstOrDefault() : null))?.Id;
        }
    }

    /// <summary>After a broken-off enqueue: true (transfer ids recorded, if any) when slskd has the batch.</summary>
    private async Task<bool> AdoptBatchAsync(DownloadJobEntity job, List<JobFileEntity> batch)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, time);
        try
        {
            var found = await slskd.GetBatchAsync(batch[0].BatchId, timeout.Token);
            if (found is null)
            {
                return false;
            }

            AssignTransfers(batch, found);
            logger.LogInformation("Enqueue for {Job} broke off but slskd has its batch ({Count} transfer(s)); keeping it",
                job.Id, found.Transfers.Count);
            return true;
        }
        catch (Exception ex) when (ex is SlskdUnavailableException
            || (ex is OperationCanceledException && timeout.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not look up the batch of {Job} after a failed enqueue", job.Id);
            return false;
        }
    }

    private Task<bool> RemoveTransfersAsync(DownloadJobEntity job, IReadOnlyCollection<JobFileEntity> files, CancellationToken ct) =>
        TransferCleanup.RemoveAsync(slskd, logger, job, files, ct);

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
