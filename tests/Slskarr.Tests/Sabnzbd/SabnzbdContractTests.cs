using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Slskarr.Data;
using Slskarr.Downloads;
using Slskarr.Nzb;
using Slskarr.Soulseek;
using Slskarr.Tests.Downloads;

namespace Slskarr.Tests.Sabnzbd;

public sealed class SabnzbdContractTests : IDisposable
{
    private const string Key = "secret";

    private readonly FakeDownloadSlskd _slskd = new();
    private readonly SlskarrFactory _factory;
    private readonly HttpClient _client;

    public SabnzbdContractTests()
    {
        _factory = new SlskarrFactory()
            .WithConfig(c =>
            {
                c["Slskarr:ApiKey"] = Key;
                c["Slskarr:Slskd:DownloadsPath"] = "/downloads";
                c["Slskarr:Paths:StorageRewriteFrom"] = "/downloads";
                c["Slskarr:Paths:StorageRewriteTo"] = "/data/media";
            })
            .WithServices(s =>
            {
                s.RemoveAll<ISlskdClient>();
                s.AddSingleton<ISlskdClient>(_slskd);
            });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private IDbContextFactory<SlskarrDb> Dbf => _factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>();

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    private Task<JsonElement> Api(string query) => Api(query, key: Key);

    private async Task<JsonElement> Api(string query, string? key) =>
        await Json(await _client.GetAsync($"/sabnzbd/api?output=json&{query}" + (key is null ? "" : $"&apikey={key}")));

    private async Task<HitEntity> SeedHit(string id = DownloadHarness.HitId)
    {
        await using var db = await Dbf.CreateDbContextAsync();
        var hit = DownloadHarness.NewHit(id);
        db.Hits.Add(hit);
        await db.SaveChangesAsync();
        return hit;
    }

    private async Task<DownloadJobEntity> SeedJob(
        DownloadState state, string title = "Frank Herbert - Dune (1965)", string category = "ebooks",
        string? storage = null, string? fail = null, DateTimeOffset? completedAt = null, long bytesDone = 0)
    {
        await using var db = await Dbf.CreateDbContextAsync();
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var id = Guid.NewGuid();
        var job = new DownloadJobEntity
        {
            Id = id, HitId = "h" + id.ToString("N"), Category = category, Title = title, Username = "peer",
            RemotePath = DownloadHarness.RemotePath, Size = 2_097_152, State = state, BytesDone = bytesDone,
            CreatedAt = _factory.Time.GetUtcNow(), LastProgressAt = _factory.Time.GetUtcNow(),
            CompletedAt = completedAt, FailMessage = fail, StoragePath = storage,
            Files = [new JobFileEntity { RemotePath = DownloadHarness.RemotePath, Size = 2_097_152, BatchId = id, State = state, BytesDone = bytesDone }],
        };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task Version_parses_as_semver_ge_0_7()
    {
        var r = await Api("mode=version");
        var v = Version.Parse(r.GetProperty("version").GetString()!);
        v.ShouldBeGreaterThanOrEqualTo(new Version(0, 7));
    }

    [Fact]
    public async Task GetConfig_passes_chaptarr_checks()
    {
        var cfg = (await Api("mode=get_config")).GetProperty("config");
        var misc = cfg.GetProperty("misc");
        misc.GetProperty("complete_dir").GetString().ShouldBe("/data/media/slskarr");
        misc.GetProperty("pre_check").GetBoolean().ShouldBeFalse();
        misc.GetProperty("enable_tv_sorting").GetBoolean().ShouldBeFalse();
        misc.GetProperty("enable_movie_sorting").GetBoolean().ShouldBeFalse();
        misc.GetProperty("enable_date_sorting").GetBoolean().ShouldBeFalse();
        misc.GetProperty("tv_categories").GetArrayLength().ShouldBe(0);
        misc.GetProperty("history_retention").GetString().ShouldBe("0");
        var cats = cfg.GetProperty("categories").EnumerateArray().ToList();
        var ebooks = cats.Single(c => c.GetProperty("name").GetString() == "ebooks");
        ebooks.GetProperty("dir").GetString().ShouldNotEndWith("*");
        ebooks.GetProperty("script").GetString().ShouldBe("Default");
        cats.ShouldContain(c => c.GetProperty("name").GetString() == "*");
        cfg.GetProperty("servers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Fullstatus_reports_rewritten_completedir()
    {
        var r = await Api("mode=fullstatus");
        r.GetProperty("status").GetProperty("completedir").GetString().ShouldBe("/data/media/slskarr");
    }

    [Fact]
    public async Task Auth_errors_match_chaptarr_strings()
    {
        var missing = await Api("mode=version", key: null);
        missing.GetProperty("status").GetBoolean().ShouldBeFalse();
        missing.GetProperty("error").GetString().ShouldBe("API Key Required");

        var wrong = await Api("mode=version", key: "nope");
        wrong.GetProperty("error").GetString().ShouldBe("API Key Incorrect");

        var ma = await Json(await _client.GetAsync($"/sabnzbd/api?output=json&mode=version&ma_username=u&ma_password={Key}"));
        ma.GetProperty("version").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task Unknown_mode_is_not_implemented()
    {
        var r = await Api("mode=bogus");
        r.GetProperty("status").GetBoolean().ShouldBeFalse();
        r.GetProperty("error").GetString().ShouldBe("not implemented");
    }

    private static MultipartFormDataContent Upload(byte[] nzb)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(nzb);
        file.Headers.ContentType = new("application/x-nzb");
        form.Add(file, "name", "release.nzb");
        return form;
    }

    private async Task<JsonElement> Addfile(byte[] nzb, string extra = "&cat=ebooks&priority=-100") =>
        await Json(await _client.PostAsync($"/sabnzbd/api?output=json&mode=addfile&apikey={Key}{extra}", Upload(nzb)));

    [Fact]
    public async Task Addfile_multipart_returns_nzo_id()
    {
        var hit = await SeedHit();
        var r = await Addfile(StubNzb.Build(hit));

        r.GetProperty("status").GetBoolean().ShouldBeTrue();
        var nzo = r.GetProperty("nzo_ids")[0].GetString()!;
        nzo.ShouldStartWith("SLSKARR_");
        _slskd.Enqueues.Count.ShouldBe(1);
        await using var db = await Dbf.CreateDbContextAsync();
        db.DownloadJobs.Single().Category.ShouldBe("ebooks");
        DownloadManager.NzoId(db.DownloadJobs.Single().Id).ShouldBe(nzo);
    }

    [Fact]
    public async Task Addfile_without_cat_uses_default_and_category_is_case_insensitive()
    {
        var hit = await SeedHit();
        (await Addfile(StubNzb.Build(hit), "")).GetProperty("status").GetBoolean().ShouldBeTrue();
        (await Addfile(StubNzb.Build(hit), "&cat=EBOOKS")).GetProperty("status").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Addfile_unknown_category_rejected()
    {
        var hit = await SeedHit();
        var r = await Addfile(StubNzb.Build(hit), "&cat=movies");
        r.GetProperty("status").GetBoolean().ShouldBeFalse();
        r.GetProperty("error").GetString().ShouldBe("Unknown category");
        _slskd.Enqueues.ShouldBeEmpty();
    }

    [Fact]
    public async Task Addfile_foreign_nzb_rejected()
    {
        var r = await Addfile("<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"><file/></nzb>"u8.ToArray());
        r.GetProperty("status").GetBoolean().ShouldBeFalse();
        r.GetProperty("error").GetString().ShouldBe("Not a Slskarr NZB");
        (await Addfile("garbage"u8.ToArray())).GetProperty("error").GetString().ShouldBe("Not a Slskarr NZB");
    }

    [Fact]
    public async Task Addfile_unknown_hit_reports_expired()
    {
        var r = await Addfile(StubNzb.Build(DownloadHarness.NewHit("gone")));
        r.GetProperty("status").GetBoolean().ShouldBeFalse();
        r.GetProperty("error").GetString().ShouldBe("Release expired or unknown");
    }

    [Fact]
    public async Task Addfile_slskd_down_reports_unavailable()
    {
        var hit = await SeedHit();
        _slskd.EnqueueThrows = true;
        var r = await Addfile(StubNzb.Build(hit));
        r.GetProperty("status").GetBoolean().ShouldBeFalse();
        r.GetProperty("error").GetString().ShouldBe("slskd unavailable");
    }

    [Fact]
    public async Task Queue_lists_active_with_category_filter()
    {
        var queued = await SeedJob(DownloadState.Queued, "Queued Book");
        var active = await SeedJob(DownloadState.Downloading, "Active Book", bytesDone: 1_048_576);
        await SeedJob(DownloadState.Queued, "Other Cat", category: "audio");
        await SeedJob(DownloadState.Completed, "Done Book");

        var all = (await Api("mode=queue")).GetProperty("queue");
        all.GetProperty("paused").GetBoolean().ShouldBeFalse();
        all.GetProperty("slots").GetArrayLength().ShouldBe(3);

        var slots = (await Api("mode=queue&category=ebooks")).GetProperty("queue").GetProperty("slots").EnumerateArray().ToList();
        slots.Count.ShouldBe(2);
        var q = slots.Single(s => s.GetProperty("nzo_id").GetString() == DownloadManager.NzoId(queued.Id));
        q.GetProperty("status").GetString().ShouldBe("Queued");
        q.GetProperty("timeleft").GetString().ShouldBe("0:00:00");
        q.GetProperty("mb").GetString().ShouldBe("2.00");
        q.GetProperty("mbleft").GetString().ShouldBe("2.00");
        q.GetProperty("filename").GetString().ShouldBe("Queued Book");
        q.GetProperty("priority").GetString().ShouldBe("Normal");
        q.GetProperty("cat").GetString().ShouldBe("ebooks");
        q.GetProperty("percentage").GetString().ShouldBe("0");
        var a = slots.Single(s => s.GetProperty("nzo_id").GetString() == DownloadManager.NzoId(active.Id));
        a.GetProperty("status").GetString().ShouldBe("Downloading");
        a.GetProperty("mbleft").GetString().ShouldBe("1.00");
        a.GetProperty("percentage").GetString().ShouldBe("50");
        a.GetProperty("index").GetInt32().ShouldBe(1);

        (await Api("mode=queue&category=*")).GetProperty("queue").GetProperty("slots").GetArrayLength().ShouldBe(3);
        var paged = (await Api("mode=queue&start=1&limit=1")).GetProperty("queue").GetProperty("slots");
        paged.GetArrayLength().ShouldBe(1);
        paged[0].GetProperty("index").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Queue_delete_removes_job_and_never_fails()
    {
        var job = await SeedJob(DownloadState.Queued);
        (await Api($"mode=queue&name=delete&value={DownloadManager.NzoId(job.Id)}")).GetProperty("status").GetBoolean().ShouldBeTrue();
        (await Api("mode=queue&name=delete&value=SLSKARR_unknown")).GetProperty("status").GetBoolean().ShouldBeTrue();
        (await Api("mode=queue")).GetProperty("queue").GetProperty("slots").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task History_completed_has_storage_path()
    {
        var done = _factory.Time.GetUtcNow().AddSeconds(91);
        var job = await SeedJob(DownloadState.Completed, storage: "/data/media/slskarr/x/Dune.epub", completedAt: done);

        var slot = (await Api("mode=history")).GetProperty("history").GetProperty("slots")[0];
        slot.GetProperty("status").GetString().ShouldBe("Completed");
        slot.GetProperty("storage").GetString().ShouldBe("/data/media/slskarr/x/Dune.epub");
        slot.GetProperty("nzo_id").GetString().ShouldBe(DownloadManager.NzoId(job.Id));
        slot.GetProperty("name").GetString().ShouldBe("Frank Herbert - Dune (1965)");
        slot.GetProperty("nzb_name").GetString().ShouldBe("Frank Herbert - Dune (1965).nzb");
        slot.GetProperty("category").GetString().ShouldBe("ebooks");
        slot.GetProperty("bytes").GetInt64().ShouldBe(2_097_152);
        slot.GetProperty("download_time").GetInt64().ShouldBe(90);
        slot.GetProperty("fail_message").GetString().ShouldBe("");
    }

    [Fact]
    public async Task History_failed_has_fail_message()
    {
        await SeedJob(DownloadState.Failed, fail: "Peer offline");
        var slot = (await Api("mode=history")).GetProperty("history").GetProperty("slots")[0];
        slot.GetProperty("status").GetString().ShouldBe("Failed");
        slot.GetProperty("fail_message").GetString().ShouldBe("Peer offline");
        slot.GetProperty("download_time").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task History_delete_with_del_files_calls_manager()
    {
        var job = await SeedJob(DownloadState.Completed, storage: "/x/a.epub");
        var r = await Api($"mode=history&name=delete&value={DownloadManager.NzoId(job.Id)}&del_files=1&archive=0");
        r.GetProperty("status").GetBoolean().ShouldBeTrue();
        _slskd.DeletedDownloadDirs.ShouldBe([StoragePaths.JobDir(job.Id)]);
        (await Api("mode=history")).GetProperty("history").GetProperty("slots").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Retry_requeues_failed_job()
    {
        var job = await SeedJob(DownloadState.Failed, fail: "x");
        await using (var db = await Dbf.CreateDbContextAsync())
        {
            db.Hits.Add(DownloadHarness.NewHit("h" + job.Id.ToString("N")));
            await db.SaveChangesAsync();
        }

        var r = await Api($"mode=retry&value={DownloadManager.NzoId(job.Id)}");
        r.GetProperty("status").GetBoolean().ShouldBeTrue();
        r.GetProperty("nzo_id").GetString().ShouldBe(DownloadManager.NzoId(job.Id));
        _slskd.Enqueues.Count.ShouldBe(1);

        var bad = await Api("mode=retry&value=SLSKARR_unknown");
        bad.GetProperty("status").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Browse_single_file_returns_exact_file_entry()
    {
        const string path = "/data/media/slskarr/abc/Dune.epub";
        await SeedJob(DownloadState.Completed, storage: path);

        var paths = (await Api($"mode=browse&name={Uri.EscapeDataString(path)}&show_files=1")).GetProperty("paths").EnumerateArray().ToList();
        paths[0].GetProperty("current_path").GetString().ShouldBe("/data/media/slskarr/abc");
        var file = paths.Single(p => p.TryGetProperty("dir", out var d) && !d.GetBoolean());
        file.GetProperty("path").GetString().ShouldBe(path);
        file.GetProperty("name").GetString().ShouldBe("Dune.epub");
    }

    [Fact]
    public async Task Browse_job_dir_lists_file_and_unknown_is_not_found()
    {
        const string path = "/data/media/slskarr/abc/Dune.epub";
        await SeedJob(DownloadState.Completed, storage: path);

        var paths = (await Api("mode=browse&name=/data/media/slskarr/abc&show_files=1")).GetProperty("paths");
        paths[0].GetProperty("current_path").GetString().ShouldBe("/data/media/slskarr/abc");
        paths[1].GetProperty("path").GetString().ShouldBe(path);

        var nf = await Api("mode=browse&name=/nowhere&show_files=1");
        nf.GetProperty("status").GetBoolean().ShouldBeFalse();
        nf.GetProperty("error").GetString().ShouldBe("Not found");
    }
}
