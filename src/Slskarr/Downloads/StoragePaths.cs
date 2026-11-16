using System.Text.RegularExpressions;
using Slskarr.Configuration;
using Slskarr.Soulseek;

namespace Slskarr.Downloads;

public static partial class StoragePaths
{
    /// <summary>
    /// Slskd destination (and files-API directory) for a job, relative to slskd's downloads dir; with a
    /// <paramref name="subdir"/> (a disc folder) the destination of that sub-batch.
    /// </summary>
    public static string JobDir(Guid jobId, string subdir = "") =>
        "slskarr/" + jobId.ToString("N") + (subdir.Length == 0 ? "" : "/" + subdir.Trim('/'));

    /// <summary>
    /// Where the finished release lives on the *arr side, prefix-rewritten: the job folder when
    /// <paramref name="remoteFile"/> is null (music), else downloads dir + job dir + file name (ebooks).
    /// </summary>
    public static string Storage(SlskdOptions slskd, PathOptions paths, Guid jobId, string? remoteFile)
    {
        var dir = $"{slskd.DownloadsPath.TrimEnd('/')}/{JobDir(jobId)}";
        return Rewrite(paths, remoteFile is null ? dir : $"{dir}/{SoulseekPath.FileName(remoteFile)}");
    }

    /// <summary>
    /// Where a Linux slskd keeps the partial file of a transfer, relative to its incomplete directory (slskd
    /// <c>DownloadService</c>: <c>CombineSafely(incomplete, SanitizePathSegment(username),
    /// GetDirectoryNameSafely(remote), GetFileNameSafely(remote))</c>). Null when the remote path names no file,
    /// in which case slskd keeps no partial either.
    /// </summary>
    public static string? IncompleteFile(string username, string remotePath)
    {
        var local = remotePath.Replace('\\', '/');
        if (local.Length == 0 || local.EndsWith('/') || StripRoot(local).Length == 0)
        {
            return null;
        }

        var file = SanitizeSegment(local.Split('/')[^1]);

        // A path without a slash is a bare file name; otherwise the remote folders, root stripped, empty ones dropped.
        var folders = local.Contains('/')
            ? StripRoot(local).Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).Select(SanitizeSegment)
            : [];

        return string.Join('/', [SanitizeSegment(username), .. folders, file]);
    }

    /// <summary>slskd <c>FileSafety.StripPathRoot</c>: drive, then UNC, then Soulseek Qt (<c>@@abcde</c>) roots.</summary>
    private static string StripRoot(string path) =>
        SoulseekQtRoot().Replace(UncRoot().Replace(DriveRoot().Replace(path, ""), ""), "");

    /// <summary>slskd <c>FileSafety.SanitizePathSegment</c> on Linux.</summary>
    private static string SanitizeSegment(string segment)
    {
        var sanitized = segment.Replace('\0', '_').Replace('/', '_').Replace('\\', '_');
        return sanitized is "." or ".." ? "_" : sanitized;
    }

    [GeneratedRegex(@"^[a-zA-Z]:[/\\]?")]
    private static partial Regex DriveRoot();

    [GeneratedRegex(@"^[/\\]{2}[^/\\]+[/\\]?")]
    private static partial Regex UncRoot();

    [GeneratedRegex(@"^@@[a-zA-Z0-9]{5,}[\/\\]?")]
    private static partial Regex SoulseekQtRoot();

    /// <summary>Applies the configured storage prefix rewrite to a slskd-side path.</summary>
    public static string Rewrite(PathOptions paths, string path)
    {
        var from = paths.StorageRewriteFrom?.TrimEnd('/');
        if (!string.IsNullOrEmpty(from) && paths.StorageRewriteTo is { } to
            && path.StartsWith(from + "/", StringComparison.Ordinal))
        {
            path = to.TrimEnd('/') + path[from.Length..];
        }

        return path;
    }
}
