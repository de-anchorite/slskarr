using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Auth;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Nzb;
using Slskarr.Profiles;
using Slskarr.Search;

namespace Slskarr.Newznab;

public static class NewznabEndpoints
{
    private const string Rss = "application/rss+xml";
    private const int MaxLimit = 100;
    private const string LogCategory = "Slskarr.Newznab";

    public static IEndpointRouteBuilder MapNewznab(this IEndpointRouteBuilder app)
    {
        app.MapGet("/newznab/api", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest req,
        IOptions<SlskarrOptions> options,
        ProfileRegistry registry,
        SearchCoordinator coordinator,
        IDbContextFactory<SlskarrDb> dbFactory,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger(LogCategory);
        var apiKey = options.Value.ApiKey;
        if (!ApiKeyCheck.IsValid(req, apiKey))
        {
            return Error(100, "Incorrect user credentials");
        }

        var q = req.Query;
        var function = q["t"].ToString().ToLowerInvariant();
        switch (function)
        {
            case "caps":
                return Results.Bytes(NewznabXml.Caps(registry.All), "application/xml");

            case "search":
            case "book":
            case "music":
                var search = new SearchRequest(
                    Clean(q["q"]), Clean(q["author"]), Clean(q["title"]), ParseCategories(q["cat"].ToString()),
                    Clean(q["artist"]), Clean(q["album"]), function);
                var offset = ParseInt(q["offset"], 0, 0, int.MaxValue);
                var limit = ParseInt(q["limit"], MaxLimit, 1, MaxLimit);
                var hits = await FindAsync(search, offset, limit, registry, coordinator, ct);
                // RSS polls come every few minutes from every *arr: not worth an Information line each.
                logger.Log(search.IsRss ? LogLevel.Debug : LogLevel.Information,
                    "Newznab t={Function} {Request}: {Count} release(s) (offset {Offset})", function, Describe(search), hits.Count, offset);
                // An empty first RSS page gets one placeholder so *arr/Prowlarr connection tests pass.
                var placeholderProfile = search.IsRss && offset == 0 && hits.Count == 0
                    ? RssAllProfiles(search) ? registry.All.FirstOrDefault() : registry.ForRequest(search)
                    : null;
                var placeholder = placeholderProfile is null ? (DateTimeOffset?)null : time.GetUtcNow();
                return Results.Bytes(NewznabXml.Feed(hits, registry, placeholderProfile, BaseUrl(req), apiKey, offset, placeholder), Rss);

            case "get":
                var id = q["id"].ToString();
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                {
                    var hit = string.IsNullOrEmpty(id) ? null : await db.Hits.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, ct);
                    if (hit is null)
                    {
                        logger.LogWarning("NZB requested for unknown release {Id} (expired, pruned or the placeholder)", id);
                        return Error(300, "No such item");
                    }

                    logger.LogInformation("NZB requested (grab): {Title} from {User} ({Id})", hit.ReleaseTitle, hit.Username, hit.Id);
                    return Results.File(StubNzb.Build(hit), "application/x-nzb", XmlText.Clean(hit.ReleaseTitle) + ".nzb");
                }

            default:
                return Error(202, "No such function");
        }
    }

    private static async Task<IReadOnlyList<HitEntity>> FindAsync(
        SearchRequest search, int offset, int limit, ProfileRegistry registry, SearchCoordinator coordinator, CancellationToken ct)
    {
        if (search.IsRss)
        {
            if (RssAllProfiles(search))
            {
                return await coordinator.RecentAsync(null, offset, limit, ct);
            }

            var profile = registry.ForRequest(search);
            return profile is null ? [] : await coordinator.RecentAsync(profile, offset, limit, ct);
        }

        var found = await coordinator.SearchAsync(search, ct);
        return [.. found.Skip(offset).Take(limit)];
    }

    // A generic t=search RSS poll without categories (Prowlarr's test) covers every profile.
    private static bool RssAllProfiles(SearchRequest s) => s.Categories.Count == 0 && s.Function is null or "search";

    /// <summary>The non-empty search parameters, e.g. <c>artist="Radiohead" album="OK Computer" cat=3000</c>.</summary>
    private static string Describe(SearchRequest s)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                parts.Add($"{name}=\"{value}\"");
            }
        }

        Add("q", s.Query);
        Add("author", s.Author);
        Add("title", s.Title);
        Add("artist", s.Artist);
        Add("album", s.Album);
        parts.Add("cat=" + (s.Categories.Count == 0 ? "(none)" : string.Join(',', s.Categories)));
        return string.Join(' ', parts);
    }

    private static IResult Error(int code, string description) => Results.Bytes(NewznabXml.Error(code, description), Rss);

    private static string BaseUrl(HttpRequest req) => $"{req.Scheme}://{req.Host}{req.PathBase}";

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int ParseInt(string? s, int fallback, int min, int max) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min ? Math.Min(v, max) : fallback;

    private static List<int> ParseCategories(string cat) =>
    [
        .. cat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? (int?)v : null)
            .OfType<int>(),
    ];
}
