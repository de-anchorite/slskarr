using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Slskarr.Data;
using Slskarr.Profiles;
using Slskarr.Search;

namespace Slskarr.Tests;

/// <summary>What the *arr asked for is logged at the Newznab and SABnzbd faces.</summary>
public sealed class EndpointLoggingTests : IDisposable
{
    private static readonly SearchRequest OkComputer = new(null, null, null, [3000, 3010, 3040], "Radiohead", "OK Computer", "music");

    private readonly LogCapture _logs = new();
    private readonly SlskarrFactory _factory;

    public EndpointLoggingTests() =>
        _factory = new SlskarrFactory().WithServices(s => s.AddSingleton<ILoggerProvider>(_logs));

    public void Dispose() => _factory.Dispose();

    private async Task SeedOkComputer()
    {
        var dbf = _factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        var now = _factory.Time.GetUtcNow();
        var profile = _factory.Services.GetRequiredService<ProfileRegistry>().ByName("music")!;
        var key = SearchKey.For(profile, OkComputer).Value;
        db.SearchJobs.Add(new SearchJobEntity
        {
            Key = key, Profile = "music", QueryText = profile.BuildQuery(OkComputer), RequestJson = SearchCoordinator.WriteRequest(OkComputer),
            State = SearchJobState.Done, CreatedAt = now, LastRequestedAt = now, CompletedAt = now,
        });
        db.Hits.Add(new HitEntity
        {
            Id = "AAAAAAAAAAAAAAAAAAAAAA", SearchKey = key, Profile = "music", Username = "peer", RemotePath = @"@@a\Music\Radiohead\OK Computer",
            Size = 300_000_000, ReleaseTitle = "Radiohead - OK Computer (1997) [FLAC]", ParsedAuthor = "Radiohead", ParsedTitle = "OK Computer",
            Format = "FLAC", Rank = 1, SeenAt = now,
        });
        await db.SaveChangesAsync();
    }

    private IEnumerable<string> Lines(LogLevel min = LogLevel.Information) =>
        _logs.Entries.Where(e => e.Level >= min && e.Category.StartsWith("Slskarr", StringComparison.Ordinal)).Select(e => e.Message);

    [Fact]
    public async Task Newznab_search_logs_request_and_result_count()
    {
        await SeedOkComputer();
        using var client = _factory.CreateClient();

        await client.GetAsync("/newznab/api?t=music&cat=3000,3010,3040&extended=1&offset=0&limit=100&q=&artist=Radiohead&album=OK%20Computer");

        var line = Lines().Single(l => l.Contains("Newznab t=music"));
        line.ShouldContain("artist=\"Radiohead\"");
        line.ShouldContain("album=\"OK Computer\"");
        line.ShouldContain("cat=3000,3010,3040");
        line.ShouldContain("1 release(s)");
    }

    [Fact]
    public async Task Rss_poll_is_not_logged_at_information()
    {
        using var client = _factory.CreateClient();

        await client.GetAsync("/newznab/api?t=search&cat=3000");

        Lines().ShouldNotContain(l => l.Contains("Newznab"));
    }

    [Fact]
    public async Task Nzb_fetch_logs_the_release()
    {
        await SeedOkComputer();
        using var client = _factory.CreateClient();

        await client.GetAsync("/newznab/api?t=get&id=AAAAAAAAAAAAAAAAAAAAAA");
        await client.GetAsync("/newznab/api?t=get&id=missing");

        Lines().ShouldContain(l => l.Contains("NZB") && l.Contains("Radiohead - OK Computer (1997) [FLAC]"));
        Lines(LogLevel.Warning).ShouldContain(l => l.Contains("NZB") && l.Contains("missing"));
    }

    [Fact]
    public async Task Sab_addfile_problems_warn()
    {
        using var client = _factory.CreateClient();

        using var bad = new MultipartFormDataContent { { new ByteArrayContent("<nzb/>"u8.ToArray()), "name", "x.nzb" } };
        await client.PostAsync("/sabnzbd/api?mode=addfile&cat=music&output=json", bad);
        using var other = new MultipartFormDataContent { { new ByteArrayContent("<nzb/>"u8.ToArray()), "name", "x.nzb" } };
        await client.PostAsync("/sabnzbd/api?mode=addfile&cat=tv&output=json", other);

        Lines(LogLevel.Warning).ShouldContain(l => l.Contains("Not a Slskarr NZB"));
        Lines(LogLevel.Warning).ShouldContain(l => l.Contains("category") && l.Contains("tv"));
    }
}
