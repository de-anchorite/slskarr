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
        CancellationToken ct)
    {
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
                // An empty first RSS page gets one placeholder so *arr/Prowlarr connection tests pass.
                var placeholderProfile = search.IsRss && offset == 0 && hits.Count == 0
                    ? RssAllProfiles(search) ? registry.All.FirstOrDefault() : registry.ForRequest(search)
                    : null;
                var placeholder = placeholderProfile is null ? (DateTimeOffset?)null : time.GetUtcNow();
                var baseUrl = BaseUrl(req);
                var browserBaseUrl = options.Value.ExternalUrl?.AbsoluteUri.TrimEnd('/') ?? baseUrl;
                return Results.Bytes(
                    NewznabXml.Feed(hits, registry, placeholderProfile, baseUrl, browserBaseUrl, apiKey, offset, placeholder), Rss);

            case "get":
                var id = q["id"].ToString();
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                {
                    var hit = string.IsNullOrEmpty(id) ? null : await db.Hits.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, ct);
                    return hit is null
                        ? Error(300, "No such item")
                        : Results.File(StubNzb.Build(hit), "application/x-nzb", XmlText.Clean(hit.ReleaseTitle) + ".nzb");
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
