using Slskarr.Configuration;
using Slskarr.Soulseek;

namespace Slskarr.Downloads;

public static class StoragePaths
{
    /// <summary>Slskd destination (and files-API directory) for a job, relative to slskd's downloads dir.</summary>
    public static string JobDir(Guid jobId) => "slskarr/" + jobId.ToString("N");

    /// <summary>Where the finished file lives on the *arr side: downloads dir + job dir + file name, prefix-rewritten.</summary>
    public static string Storage(SlskdOptions slskd, PathOptions paths, Guid jobId, string remotePath)
    {
        var path = $"{slskd.DownloadsPath.TrimEnd('/')}/{JobDir(jobId)}/{SoulseekPath.FileName(remotePath)}";
        return Rewrite(paths, path);
    }

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
