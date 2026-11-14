namespace Slskarr.Soulseek;

public sealed record SlskdFile(string Filename, long Size, string? Extension);

public sealed record SlskdSearchResponse(
    string Username,
    bool HasFreeUploadSlot,
    int QueueLength,
    int UploadSpeed,
    IReadOnlyList<SlskdFile> Files);
