namespace Slskarr.Soulseek;

/// <summary>Typed client for the slskd HTTP API (<c>/api/v0</c>).</summary>
/// <remarks>Network failures, timeouts and unexpected statuses surface as <see cref="SlskdUnavailableException"/>.</remarks>
public interface ISlskdClient
{
    /// <summary>
    /// Starts a search with a client-generated id. Returns once slskd has accepted the search (state Requested);
    /// callers poll <see cref="GetSearchAsync"/> until <c>IsComplete</c>.
    /// </summary>
    Task<Guid> StartSearchAsync(string text, int timeoutSeconds, int responseLimit, CancellationToken ct);

    /// <summary>Returns null when slskd no longer has the search (deleted or expired).</summary>
    Task<SlskdSearch?> GetSearchAsync(Guid id, bool includeResponses, CancellationToken ct);

    Task DeleteSearchAsync(Guid id, CancellationToken ct);

    Task<SlskdEnqueueResult> EnqueueBatchAsync(
        Guid batchId, string username, IReadOnlyList<SlskdEnqueueFile> files, string destination, CancellationToken ct);

    /// <summary>Returns null when slskd has no such batch.</summary>
    Task<SlskdBatch?> GetBatchAsync(Guid batchId, CancellationToken ct);

    /// <summary>Returns null when slskd cannot report a position (unknown transfer).</summary>
    Task<int?> GetPlaceInQueueAsync(string username, Guid transferId, CancellationToken ct);

    Task CancelDownloadAsync(string username, Guid transferId, bool remove, CancellationToken ct);

    Task<string> GetVersionAsync(CancellationToken ct);

    /// <summary>Lists files (recursively) under a directory relative to slskd's downloads root; null when absent.</summary>
    Task<IReadOnlyList<SlskdLocalFile>?> ListDownloadDirAsync(string relativeDir, CancellationToken ct);

    Task<SlskdDeleteResult> DeleteDownloadDirAsync(string relativeDir, CancellationToken ct);

    Task<SlskdDeleteResult> DeleteIncompleteFileAsync(string relativePath, CancellationToken ct);
}
