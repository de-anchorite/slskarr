namespace Slskarr.Data;

public enum SearchJobState { Queued, Running, Done, Failed, Expired }

public enum DownloadState { Queued, Downloading, Completed, Failed, Deleted }

/// <summary>A search result file. <see cref="Id"/> is the 22-char URL-safe base64 of a Guid.</summary>
public sealed class HitEntity
{
    public required string Id { get; set; }

    public required string SearchKey { get; set; }

    public required string Profile { get; set; }

    public required string Username { get; set; }

    public required string RemotePath { get; set; }

    public long Size { get; set; }

    public required string ReleaseTitle { get; set; }

    public string? ParsedAuthor { get; set; }

    public string? ParsedTitle { get; set; }

    public int? Year { get; set; }

    public required string Format { get; set; }

    public bool FreeSlot { get; set; }

    public int QueueLength { get; set; }

    public int UploadSpeed { get; set; }

    public int Rank { get; set; }

    public DateTimeOffset SeenAt { get; set; }
}

public sealed class SearchJobEntity
{
    public required string Key { get; set; }

    public required string Profile { get; set; }

    public required string QueryText { get; set; }

    public required string RequestJson { get; set; }

    public SearchJobState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastRequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? SlskdSearchId { get; set; }

    public string? Error { get; set; }
}

/// <summary>A queued download. <see cref="Id"/> is also the slskd batch id; nzo_id is <c>"SLSKARR_" + Id:N</c>.</summary>
public sealed class DownloadJobEntity
{
    public Guid Id { get; set; }

    public required string HitId { get; set; }

    public required string Category { get; set; }

    public required string Title { get; set; }

    public required string Username { get; set; }

    public required string RemotePath { get; set; }

    public long Size { get; set; }

    public DownloadState State { get; set; }

    public long BytesDone { get; set; }

    public int? PlaceInQueue { get; set; }

    public DateTimeOffset LastProgressAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? FailMessage { get; set; }

    public Guid? SlskdTransferId { get; set; }

    public string? StoragePath { get; set; }
}
