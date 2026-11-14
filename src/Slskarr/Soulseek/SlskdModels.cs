namespace Slskarr.Soulseek;

public sealed record SlskdFile(string Filename, long Size, string? Extension);

public sealed record SlskdSearchResponse(
    string Username,
    bool HasFreeUploadSlot,
    int QueueLength,
    int UploadSpeed,
    IReadOnlyList<SlskdFile> Files);

public sealed record SlskdSearch(
    Guid Id,
    string State,
    bool IsComplete,
    int ResponseCount,
    IReadOnlyList<SlskdSearchResponse>? Responses);

public sealed record SlskdTransfer(
    Guid Id,
    Guid? BatchId,
    string Username,
    string Filename,
    long Size,
    string State,
    long BytesTransferred,
    double AverageSpeed,
    int? PlaceInQueue,
    string? Exception);

public sealed record SlskdBatch(Guid Id, string Username, IReadOnlyList<SlskdTransfer> Transfers);

public sealed record SlskdEnqueueResult(bool Ok, SlskdBatch? Batch, string? Error);

public sealed record SlskdLocalFile(string Name, long Length);

public enum SlskdDeleteResult
{
    Deleted,
    NotFound,
    Forbidden,
}
