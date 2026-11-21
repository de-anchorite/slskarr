using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Auth;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Downloads;
using Slskarr.Nzb;
using Slskarr.Profiles;
using Slskarr.Soulseek;

namespace Slskarr.Sabnzbd;

/// <summary>The SABnzbd download-client face: <c>/sabnzbd/api?mode=…</c>, JSON only.</summary>
public static class SabnzbdEndpoints
{
    private const string LogCategory = "Slskarr.Sabnzbd";

    public static IEndpointRouteBuilder MapSabnzbd(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/sabnzbd/api", ["GET", "POST"], HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest req,
        IOptions<SlskarrOptions> options,
        ProfileRegistry registry,
        DownloadManager manager,
        IDbContextFactory<SlskarrDb> dbFactory,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var opts = options.Value;
        var logger = loggers.CreateLogger(LogCategory);
        if (AuthError(req, opts.ApiKey) is { } authError)
        {
            return Results.Json(SabnzbdJson.Error(authError));
        }

        try
        {
            return Results.Json(await DispatchAsync(req, opts, registry, manager, dbFactory, logger, ct));
        }
        catch (SlskdUnavailableException ex)
        {
            logger.LogWarning("SABnzbd mode={Mode} failed: slskd unavailable: {Error}", Param(req, "mode"), ex.Message);
            return Results.Json(SabnzbdJson.Error("slskd unavailable"));
        }
    }

    private static async Task<object> DispatchAsync(
        HttpRequest req, SlskarrOptions opts, ProfileRegistry registry, DownloadManager manager,
        IDbContextFactory<SlskarrDb> dbFactory, ILogger logger, CancellationToken ct)
    {
        switch (Param(req, "mode").ToLowerInvariant())
        {
            case "version":
                return SabnzbdJson.Version();

            case "get_config":
                return SabnzbdJson.Config(CompleteDir(opts), registry.All.Select(p => p.SabCategory));

            case "fullstatus":
                return SabnzbdJson.FullStatus(CompleteDir(opts));

            case "addfile":
                return await AddFileAsync(req, registry, manager, logger, ct);

            case "queue":
                if (IsDelete(req))
                {
                    return await DeleteAsync(req, manager, ct);
                }

                var active = await manager.ActiveAsync(CategoryFilter(req, registry), ct);
                var (qStart, qPage) = Page(req, active);
                return SabnzbdJson.Queue(qPage, qStart);

            case "history":
                if (IsDelete(req))
                {
                    return await DeleteAsync(req, manager, ct);
                }

                var done = (await manager.HistoryAsync(CategoryFilter(req, registry), ct)).Reverse().ToList();
                return SabnzbdJson.History(Page(req, done).Items);

            case "retry":
                var retry = await manager.RetryAsync(Param(req, "value"), ct);
                if (!retry.Ok)
                {
                    logger.LogWarning("Retry of {NzoId} refused: {Error}", Param(req, "value"), retry.Error);
                }

                return retry.Ok ? SabnzbdJson.Retried(retry.NzoId!) : SabnzbdJson.Error(retry.Error ?? "Retry failed");

            case "browse":
                return await BrowseAsync(Param(req, "name"), dbFactory, ct);

            default:
                return SabnzbdJson.Error("not implemented");
        }
    }

    private static async Task<object> AddFileAsync(
        HttpRequest req, ProfileRegistry registry, DownloadManager manager, ILogger logger, CancellationToken ct)
    {
        var cat = Param(req, "cat");
        var profile = string.IsNullOrWhiteSpace(cat) ? registry.All.FirstOrDefault() : registry.ForSabCategory(cat);
        if (profile is null)
        {
            logger.LogWarning("Add refused: unknown category \"{Category}\" (expected one of {Known})",
                cat, string.Join(", ", registry.All.Select(p => p.SabCategory)));
            return SabnzbdJson.Error("Unknown category");
        }

        var file = req.HasFormContentType ? (await req.ReadFormAsync(ct)).Files["name"] : null;
        string? hitId = null;
        if (file is not null)
        {
            await using var stream = file.OpenReadStream();
            hitId = StubNzb.ReadHitId(stream);
        }

        if (string.IsNullOrEmpty(hitId))
        {
            logger.LogWarning("Add refused: Not a Slskarr NZB ({File})", file?.FileName ?? "no file uploaded");
            return SabnzbdJson.Error("Not a Slskarr NZB");
        }

        var result = await manager.AddAsync(hitId, profile.SabCategory, ct);
        return result.Ok ? SabnzbdJson.Added(result.NzoId!) : SabnzbdJson.Error(result.Error ?? "Add failed");
    }

    /// <summary>SAB's delete never fails; ids may be comma separated; unknown ids are ignored.</summary>
    private static async Task<object> DeleteAsync(HttpRequest req, DownloadManager manager, CancellationToken ct)
    {
        var deleteFiles = Param(req, "del_files") is "1" or "true";
        foreach (var id in Param(req, "value").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await manager.DeleteAsync(id, deleteFiles, ct);
        }

        return SabnzbdJson.Ok();
    }

    /// <summary>Answers from stored storage paths only; no filesystem access.</summary>
    private static async Task<object> BrowseAsync(string name, IDbContextFactory<SlskarrDb> dbFactory, CancellationToken ct)
    {
        var requested = name.Length > 1 ? name.TrimEnd('/') : name;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.DownloadJobs.AsNoTracking()
            .Where(j => j.State != DownloadState.Deleted && j.StoragePath != null
                && (j.StoragePath == requested || j.StoragePath.StartsWith(requested + "/")))
            .Select(j => j.StoragePath!)
            .ToListAsync(ct);

        var exact = candidates.Where(p => p == requested).ToList();
        if (exact.Count > 0)
        {
            return SabnzbdJson.Browse(Parent(requested), exact.Select(Entry));
        }

        var inDir = candidates.Where(p => Parent(p) == requested).ToList();
        return inDir.Count > 0 ? SabnzbdJson.Browse(requested, inDir.Select(Entry)) : SabnzbdJson.Error("Not found");
    }

    private static (string Name, string Path) Entry(string path) => (path[(path.LastIndexOf('/') + 1)..], path);

    private static string Parent(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "/";

    private static string CompleteDir(SlskarrOptions opts) =>
        StoragePaths.Rewrite(opts.Paths, opts.Slskd.DownloadsPath.TrimEnd('/') + "/slskarr");

    private static string? CategoryFilter(HttpRequest req, ProfileRegistry registry)
    {
        var cat = Param(req, "category");
        return string.IsNullOrWhiteSpace(cat) || cat == "*" ? null : registry.ForSabCategory(cat)?.SabCategory ?? cat;
    }

    private static bool IsDelete(HttpRequest req) => string.Equals(Param(req, "name"), "delete", StringComparison.OrdinalIgnoreCase);

    /// <summary>SAB paging: <c>start</c> default 0, <c>limit</c> default 0 meaning unlimited.</summary>
    private static (int Start, IReadOnlyList<DownloadJobEntity> Items) Page(HttpRequest req, IReadOnlyList<DownloadJobEntity> all)
    {
        var start = Math.Max(0, Int(Param(req, "start")));
        var limit = Math.Max(0, Int(Param(req, "limit")));
        var page = all.Skip(start);
        return (start, [.. limit > 0 ? page.Take(limit) : page]);
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Null when authorised, else SAB's error text. Accepts <c>apikey</c> or <c>ma_password</c> (== the key).</summary>
    private static string? AuthError(HttpRequest req, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var supplied = Param(req, "apikey");
        if (supplied.Length == 0)
        {
            supplied = Param(req, "ma_password");
        }

        if (supplied.Length == 0)
        {
            return "API Key Required";
        }

        return ApiKeyCheck.Matches(supplied, configured) ? null : "API Key Incorrect";
    }

    /// <summary>A parameter from the query string, else the form body.</summary>
    private static string Param(HttpRequest req, string name)
    {
        string? v = req.Query[name];
        if (string.IsNullOrEmpty(v) && req.HasFormContentType)
        {
            v = req.Form[name];
        }

        return v ?? "";
    }
}
