using Slskarr.Data;
using Slskarr.Soulseek;

namespace Slskarr.Downloads;

/// <summary>Removal of slskd transfer records, shared by the manager and the monitor.</summary>
internal static class TransferCleanup
{
    /// <summary>
    /// Cancels and clears the slskd transfer records of the given files (404 is success). While the job is active,
    /// a file with no recorded transfer is looked up in its batch. Never throws for slskd outages; returns false
    /// (keeping the transfer ids of the batches concerned) when removal could not be confirmed.
    /// </summary>
    public static async Task<bool> RemoveAsync(ISlskdClient slskd, ILogger logger, DownloadJobEntity job, IReadOnlyCollection<JobFileEntity> files, CancellationToken ct)
    {
        var confirmed = true;
        var active = job.State is DownloadState.Queued or DownloadState.Downloading;
        foreach (var batch in files.GroupBy(f => f.BatchId))
        {
            try
            {
                var transferIds = batch.Select(f => f.SlskdTransferId).OfType<Guid>().ToList();
                if (active && batch.Any(f => f.SlskdTransferId is null)
                    && await slskd.GetBatchAsync(batch.Key, ct) is { } found)
                {
                    transferIds = [.. transferIds.Union(found.Transfers.Select(t => t.Id))];
                }

                foreach (var transferId in transferIds)
                {
                    await slskd.CancelDownloadAsync(job.Username, transferId, remove: true, ct);
                }
            }
            catch (SlskdUnavailableException ex)
            {
                logger.LogWarning(ex, "slskd unreachable while removing transfers for {Job}", job.Id);
                confirmed = false;
                continue;
            }

            foreach (var file in batch)
            {
                file.SlskdTransferId = null;
            }
        }

        return confirmed;
    }
}
