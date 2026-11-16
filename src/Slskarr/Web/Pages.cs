using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using Slskarr.Data;
using Slskarr.Downloads;
using Slskarr.Search;
using Slskarr.Soulseek;

namespace Slskarr.Web;

/// <summary>Read-only HTML pages: the status page at <c>/</c> and a release detail page. No API key, no secrets.</summary>
public static class Pages
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(3);

    private const string Css =
        ":root{color-scheme:light dark;--bg:#fff;--fg:#1b1b1b;--muted:#666;--line:#ddd;--warn:#fff3cd}" +
        "@media(prefers-color-scheme:dark){:root{--bg:#161616;--fg:#e6e6e6;--muted:#999;--line:#333;--warn:#4a3d10}}" +
        "body{background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,sans-serif;margin:0 auto;max-width:60rem;padding:1rem}" +
        "table{border-collapse:collapse;width:100%;margin:.5rem 0 1.5rem}th,td{border-bottom:1px solid var(--line);padding:.25rem .5rem;text-align:left;vertical-align:top;overflow-wrap:anywhere}" +
        "th{color:var(--muted);font-weight:600}code{font-family:ui-monospace,monospace}.warn{background:var(--warn);padding:.5rem .75rem;border-radius:4px}";

    public static IEndpointRouteBuilder MapPages(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", StatusAsync);
        app.MapGet("/release/{hitId}", ReleaseAsync);
        return app;
    }

    private static async Task<IResult> StatusAsync(
        SearchCoordinator coordinator,
        FileManagementStatus fileStatus,
        ISlskdClient slskd,
        IDbContextFactory<SlskarrDb> dbFactory,
        TimeProvider time,
        CancellationToken ct)
    {
        var q = coordinator.Status();
        var version = await VersionAsync(slskd, time, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var searches = await db.SearchJobs.AsNoTracking()
            .OrderByDescending(j => j.LastRequestedAt)
            .Take(20)
            .ToListAsync(ct);
        var jobs = await db.DownloadJobs.AsNoTracking()
            .Where(j => j.State == DownloadState.Queued || j.State == DownloadState.Downloading)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);

        var h = new Html();
        if (fileStatus.RemoteDeleteForbidden)
        {
            h.Raw("<p class=\"warn\">").Text("slskd refused to delete files: enable remote_file_management in slskd so Slskarr can remove imported downloads.").Raw("</p>");
        }

        h.Raw("<h2>Queue</h2><table>");
        Row(h, "Slskarr version", AppVersion.Current);
        Row(h, "slskd version", version);
        Row(h, "Queued searches", q.Queued.ToString(CultureInfo.InvariantCulture));
        Row(h, "Running search", q.Running ?? "none");
        Row(h, "Oldest queued age", q.OldestAge is { } age ? $"{(long)age.TotalSeconds}s" : "none");
        Row(h, "Expired in last 24h", q.ExpiredLast24h.ToString(CultureInfo.InvariantCulture));
        Row(h, "Throttle", q.ThrottledUntilNextHour ? "hourly cap reached" : "ok");
        h.Raw("</table><h2>Last 20 searches</h2><table><tr><th>Query</th><th>State</th><th>Created</th><th>Completed</th><th>Error</th></tr>");
        foreach (var s in searches)
        {
            Row(h, s.QueryText, s.State.ToString(), Iso(s.CreatedAt), Iso(s.CompletedAt), s.Error ?? "");
        }

        h.Raw("</table><h2>Active downloads</h2><table><tr><th>Title</th><th>State</th><th>Peer</th><th>Progress</th><th>Created</th></tr>");
        foreach (var j in jobs)
        {
            Row(h, j.Title, j.State.ToString(), j.Username, $"{j.BytesDone}/{j.Size}", Iso(j.CreatedAt));
        }

        h.Raw("</table>");
        return Page("Slskarr", h);
    }

    private static async Task<string> VersionAsync(ISlskdClient slskd, TimeProvider time, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(VersionTimeout, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await slskd.GetVersionAsync(cts.Token);
        }
        catch (Exception ex) when (ex is SlskdUnavailableException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return "unreachable";
        }
    }

    private static async Task<IResult> ReleaseAsync(string hitId, IDbContextFactory<SlskarrDb> dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hit = await db.Hits.AsNoTracking().Include(x => x.Files).SingleOrDefaultAsync(x => x.Id == hitId, ct);
        if (hit is null)
        {
            return Results.NotFound();
        }

        var jobs = (await db.DownloadJobs.AsNoTracking()
                .Where(j => j.HitId == hitId && j.State != DownloadState.Deleted)
                .ToListAsync(ct))
            .OrderBy(j => j.CreatedAt)
            .ToList();

        var h = new Html();
        h.Raw("<table>");
        Row(h, "Peer", hit.Username);
        Row(h, "Remote path", hit.RemotePath);
        Row(h, "Size (bytes)", hit.Size.ToString(CultureInfo.InvariantCulture));
        Row(h, "Format", hit.Format);
        Row(h, "Free slot", hit.FreeSlot ? "yes" : "no");
        Row(h, "Queue length", hit.QueueLength.ToString(CultureInfo.InvariantCulture));
        Row(h, "Upload speed", hit.UploadSpeed.ToString(CultureInfo.InvariantCulture));
        Row(h, "Search key", hit.SearchKey);
        Row(h, "Seen at", Iso(hit.SeenAt));
        Row(h, "Rank", hit.Rank.ToString(CultureInfo.InvariantCulture));
        h.Raw("</table><h2>Files</h2><table><tr><th>File</th><th>Size (bytes)</th><th>Audio</th><th>Length</th></tr>");
        if (hit.Files.Count == 0)
        {
            Row(h, LastSegment(hit.RemotePath), hit.Size.ToString(CultureInfo.InvariantCulture), "", "");
        }

        foreach (var f in hit.Files.OrderBy(f => f.Subdir, StringComparer.Ordinal).ThenBy(f => f.RemotePath, StringComparer.Ordinal))
        {
            var name = LastSegment(f.RemotePath);
            Row(
                h,
                f.Subdir.Length > 0 ? $"{f.Subdir}/{name}" : name,
                f.Size.ToString(CultureInfo.InvariantCulture),
                AudioDetail(f),
                f.LengthSeconds is { } len ? $"{len / 60}:{len % 60:D2}" : "");
        }

        h.Raw("</table><h2>Jobs</h2><table><tr><th>nzo_id</th><th>State</th><th>Failure</th></tr>");
        foreach (var j in jobs)
        {
            Row(h, DownloadManager.NzoId(j.Id), j.State.ToString(), j.FailMessage ?? "");
        }

        h.Raw("</table>");
        return Page("Release", h);
    }

    private static string LastSegment(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    private static string AudioDetail(HitFileEntity f) =>
        f.BitDepth is { } depth && f.SampleRate is { } rate
            ? $"{depth.ToString(CultureInfo.InvariantCulture)}-bit / {rate.ToString(CultureInfo.InvariantCulture)} Hz"
            : f.BitRate is { } kbps ? $"{kbps.ToString(CultureInfo.InvariantCulture)} kbps" : "";

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset? t) => t is { } v ? Iso(v) : "";

    private static void Row(Html h, params string[] cells)
    {
        h.Raw("<tr>");
        foreach (var c in cells)
        {
            h.Raw("<td>").Text(c).Raw("</td>");
        }

        h.Raw("</tr>");
    }

    private static IResult Page(string title, Html body) =>
        Results.Content(
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            $"<title>{title}</title><style>{Css}</style></head><body><h1>{title}</h1>{body}</body></html>",
            "text/html; charset=utf-8",
            Encoding.UTF8);

    private sealed class Html
    {
        private readonly StringBuilder _sb = new();

        public Html Raw(string markup)
        {
            _sb.Append(markup);
            return this;
        }

        public Html Text(string text)
        {
            _sb.Append(HtmlEncoder.Default.Encode(text));
            return this;
        }

        public override string ToString() => _sb.ToString();
    }
}
