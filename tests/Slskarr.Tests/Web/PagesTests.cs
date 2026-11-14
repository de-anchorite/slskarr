using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Slskarr.Data;
using Slskarr.Downloads;

namespace Slskarr.Tests.Web;

public sealed class PagesTests : IDisposable
{
    private const string AppKey = "APPKEY-distinct-7f3a";
    private const string SlskdKey = "SLSKDKEY-distinct-91bc";

    private readonly List<IDisposable> _disposables = [];

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private (SlskarrFactory Factory, HttpClient Client) Start(Func<HttpResponseMessage>? slskd = null)
    {
        var factory = new SlskarrFactory().WithConfig(c =>
        {
            c["Slskarr:ApiKey"] = AppKey;
            c["Slskarr:Slskd:ApiKey"] = SlskdKey;
        });
        if (slskd is not null)
        {
            factory.WithSlskdHandler(() => new StubHandler(slskd));
        }

        var client = factory.CreateClient();
        _disposables.Add(client);
        _disposables.Add(factory);
        return (factory, client);
    }

    private static HttpResponseMessage Down() => new(HttpStatusCode.ServiceUnavailable);

    private static HttpResponseMessage Version() => new(HttpStatusCode.OK) { Content = new StringContent("\"0.26.3\"") };

    private static void Seed(SlskarrFactory f, Action<SlskarrDb> seed)
    {
        using var db = f.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>().CreateDbContext();
        seed(db);
        db.SaveChanges();
    }

    private static HitEntity Hit(string id, string path) => new()
    {
        Id = id, SearchKey = "frank herbert dune", Profile = "ebook", Username = "peer<b>1", RemotePath = path,
        Size = 1_234_567, ReleaseTitle = "t", Format = "EPUB", FreeSlot = true, QueueLength = 3, UploadSpeed = 4242,
        Rank = 7, SeenAt = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero),
    };

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    [Fact]
    public async Task Release_page_shows_remote_path_html_encoded()
    {
        var (f, c) = Start();
        var path = @"@@abcde\Books\<script>alert(1)</script>\Dune.epub";
        Seed(f, db => db.Hits.Add(Hit("hit1", path)));

        var res = await c.GetAsync("/release/hit1");

        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.ToString().ShouldBe("text/html; charset=utf-8");
        var html = await res.Content.ReadAsStringAsync();
        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.ShouldContain(@"@@abcde\Books\");
        html.ShouldContain("peer&lt;b&gt;1");
        html.ShouldContain("1234567");
        html.ShouldContain("EPUB");
        html.ShouldContain("4242");
        html.ShouldContain("frank herbert dune");
        html.ShouldContain("2026-01-01T12:30:00Z");
    }

    [Fact]
    public async Task Release_page_shows_non_deleted_job_state()
    {
        var (f, c) = Start();
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();
        Seed(f, db =>
        {
            db.Hits.Add(Hit("hit1", @"@@a\x.epub"));
            db.DownloadJobs.Add(Job(live, "hit1", DownloadState.Failed, "peer said <no>"));
            db.DownloadJobs.Add(Job(gone, "hit1", DownloadState.Deleted, null));
        });

        var html = await (await c.GetAsync("/release/hit1")).Content.ReadAsStringAsync();

        html.ShouldContain($"SLSKARR_{live:N}");
        html.ShouldContain("Failed");
        html.ShouldContain("peer said &lt;no&gt;");
        html.ShouldNotContain($"SLSKARR_{gone:N}");
    }

    private static DownloadJobEntity Job(Guid id, string hitId, DownloadState state, string? fail) => new()
    {
        Id = id, HitId = hitId, Category = "ebooks", Title = "T<i>", Username = "peer", RemotePath = @"@@a\x.epub",
        State = state, FailMessage = fail, CreatedAt = DateTimeOffset.UnixEpoch, LastProgressAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Release_unknown_returns_404()
    {
        var (_, c) = Start();

        (await c.GetAsync("/release/nope")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Status_page_shows_unreachable_when_slskd_down()
    {
        var (_, c) = Start(Down);

        var res = await c.GetAsync("/");

        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.ToString().ShouldBe("text/html; charset=utf-8");
        (await res.Content.ReadAsStringAsync()).ShouldContain("unreachable");
    }

    [Fact]
    public async Task Status_page_shows_slskd_version_when_up()
    {
        var (_, c) = Start(Version);

        (await (await c.GetAsync("/")).Content.ReadAsStringAsync()).ShouldContain("0.26.3");
    }

    [Fact]
    public async Task Status_page_lists_last_20_searches_and_active_jobs_encoded()
    {
        var (f, c) = Start(Down);
        Seed(f, db =>
        {
            for (var i = 0; i < 25; i++)
            {
                db.SearchJobs.Add(new SearchJobEntity
                {
                    Key = $"key{i:D2}", Profile = "ebook", QueryText = $"q{i:D2} <b>", RequestJson = "{}",
                    State = i == 24 ? SearchJobState.Failed : SearchJobState.Done,
                    Error = i == 24 ? "boom <x>" : null,
                    CreatedAt = DateTimeOffset.UnixEpoch, LastRequestedAt = DateTimeOffset.UnixEpoch.AddMinutes(i),
                });
            }

            db.Hits.Add(Hit("hit1", @"@@a\x.epub"));
            db.DownloadJobs.Add(Job(Guid.NewGuid(), "hit1", DownloadState.Downloading, null));
        });

        var html = await (await c.GetAsync("/")).Content.ReadAsStringAsync();

        html.ShouldContain("q24 &lt;b&gt;");
        html.ShouldContain("q05 &lt;b&gt;");
        html.ShouldNotContain("q04 &lt;b&gt;");
        html.ShouldContain("boom &lt;x&gt;");
        html.ShouldContain("T&lt;i&gt;");
        html.ShouldContain("Downloading");
    }

    [Fact]
    public async Task Status_page_shows_remote_file_management_notice_when_forbidden()
    {
        var (f, c) = Start(Down);
        var before = await (await c.GetAsync("/")).Content.ReadAsStringAsync();
        before.ShouldNotContain("remote_file_management");

        f.Services.GetRequiredService<FileManagementStatus>().MarkRemoteDeleteForbidden();
        var after = await (await c.GetAsync("/")).Content.ReadAsStringAsync();

        after.ShouldContain("slskd refused to delete files: enable remote_file_management in slskd so Slskarr can remove imported downloads.");
    }

    [Fact]
    public async Task Pages_never_include_api_keys()
    {
        var (f, c) = Start(Version);
        Seed(f, db => db.Hits.Add(Hit("hit1", @"@@a\x.epub")));

        foreach (var url in new[] { "/", "/release/hit1" })
        {
            var html = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
            html.ShouldNotContain(AppKey);
            html.ShouldNotContain(SlskdKey);
        }
    }
}
