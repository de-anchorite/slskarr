using System.Globalization;
using Slskarr.Data;
using Slskarr.Downloads;

namespace Slskarr.Sabnzbd;

/// <summary>Response shapes the SABnzbd face emits. Field names are SAB's (snake_case), and numbers SAB sends as strings stay strings.</summary>
public static class SabnzbdJson
{
    private const double Mb = 1024d * 1024d;

    public static object Error(string message) => new { status = false, error = message };

    public static object Ok() => new { status = true };

    public static object Version() => new { version = "4.3.0" };

    public static object Config(string completeDir, IEnumerable<string> categories) => new
    {
        config = new
        {
            misc = new
            {
                complete_dir = completeDir,
                pre_check = false,
                enable_tv_sorting = false,
                enable_movie_sorting = false,
                enable_date_sorting = false,
                tv_categories = Array.Empty<string>(),
                movie_categories = Array.Empty<string>(),
                date_categories = Array.Empty<string>(),
                history_retention = "0",
            },
            categories = new[] { Category("*", "") }.Concat(categories.Select(c => Category(c, c))).ToArray(),
            servers = Array.Empty<string>(),
        },
    };

    public static object FullStatus(string completeDir) => new { status = new { completedir = completeDir } };

    public static object Added(string nzoId) => new { status = true, nzo_ids = new[] { nzoId } };

    public static object Retried(string nzoId) => new { status = true, nzo_id = nzoId };

    public static object Queue(IEnumerable<DownloadJobEntity> slots, int firstIndex) => new
    {
        queue = new { paused = false, slots = slots.Select((j, i) => QueueSlot(j, firstIndex + i)).ToArray() },
    };

    public static object History(IEnumerable<DownloadJobEntity> slots) => new
    {
        history = new { paused = false, slots = slots.Select(HistorySlot).ToArray() },
    };

    public static object Browse(string currentPath, IEnumerable<(string Name, string Path)> files) => new
    {
        paths = new object[] { new { current_path = currentPath } }
            .Concat(files.Select(f => new { name = f.Name, path = f.Path, dir = false }))
            .ToArray(),
    };

    private static object Category(string name, string dir) => new { name, dir, priority = 0, pp = "", script = "Default" };

    private static object QueueSlot(DownloadJobEntity j, int index)
    {
        var left = Math.Max(0, j.Size - j.BytesDone);
        var pct = j.Size > 0 ? (int)Math.Clamp(j.BytesDone * 100 / j.Size, 0, 100) : 0;
        return new
        {
            status = j.State == DownloadState.Downloading ? "Downloading" : "Queued",
            index,
            timeleft = "0:00:00",
            mb = Format(j.Size / Mb),
            mbleft = Format(left / Mb),
            filename = j.Title,
            priority = "Normal",
            cat = j.Category,
            percentage = pct.ToString(CultureInfo.InvariantCulture),
            nzo_id = DownloadManager.NzoId(j.Id),
        };
    }

    private static object HistorySlot(DownloadJobEntity j) => new
    {
        fail_message = j.FailMessage ?? "",
        bytes = j.Size,
        category = j.Category,
        nzb_name = j.Title + ".nzb",
        download_time = j.CompletedAt is { } done ? (long)(done - j.CreatedAt).TotalSeconds : 0L,
        storage = j.StoragePath ?? "",
        status = j.State == DownloadState.Completed ? "Completed" : "Failed",
        nzo_id = DownloadManager.NzoId(j.Id),
        name = j.Title,
    };

    private static string Format(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
