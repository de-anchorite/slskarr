using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Slskarr.Data;

namespace Slskarr.Tests.Data;

public sealed class SlskarrDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slskarr-db-tests", Guid.NewGuid().ToString("N"));

    private SlskarrDb NewDb()
    {
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<SlskarrDb>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "slskarr.db")};Pooling=False")
            .Options;
        var db = new SlskarrDb(options);
        db.Database.Migrate();
        return db;
    }

    private static HitEntity Hit(string id, string path, DateTimeOffset seenAt) => new()
    {
        Id = id,
        SearchKey = "key1",
        Profile = "ebook",
        Username = "peer",
        RemotePath = path,
        Size = 1234,
        ReleaseTitle = "Title",
        Format = "epub",
        SeenAt = seenAt,
    };

    [Fact]
    public void Hits_unique_per_search_peer_path()
    {
        using var db = NewDb();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.Hits.Add(Hit("a", @"@@x\Books\a.epub", t));
        db.SaveChanges();

        db.Hits.Add(Hit("b", @"@@x\Books\a.epub", t));
        Should.Throw<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Hits_ordered_by_SeenAt_desc()
    {
        using var db = NewDb();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.Hits.AddRange(
            Hit("a", "p1", t),
            Hit("b", "p2", t.AddHours(2)),
            Hit("c", "p3", t.AddHours(1)),
            Hit("d", "p4", t.AddHours(-30)));
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var ids = db.Hits.OrderByDescending(h => h.SeenAt).Select(h => h.Id).ToList();
        ids.ShouldBe(["b", "c", "a", "d"]);

        var seen = db.Hits.Single(h => h.Id == "b").SeenAt;
        seen.ShouldBe(t.AddHours(2));
        seen.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void DownloadJob_roundtrip()
    {
        var id = Guid.NewGuid();
        var transfer = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
        using (var db = NewDb())
        {
            db.DownloadJobs.Add(new DownloadJobEntity
            {
                Id = id,
                HitId = "hit",
                Category = "ebooks",
                Title = "T",
                Username = "peer",
                RemotePath = @"@@x\a.epub",
                Files = [new JobFileEntity { RemotePath = @"@@x\a.epub", Size = 99, Subdir = "", BatchId = id, SlskdTransferId = transfer, State = DownloadState.Downloading, BytesDone = 40, PlaceInQueue = 3 }],
                Size = 99,
                State = DownloadState.Downloading,
                BytesDone = 40,
                PlaceInQueue = 3,
                LastProgressAt = t,
                CreatedAt = t,
                CompletedAt = null,
                FailMessage = "oops",
                StoragePath = "/data/x",
            });
            db.SearchJobs.Add(new SearchJobEntity
            {
                Key = "k",
                Profile = "ebook",
                QueryText = "q",
                RequestJson = "{}",
                State = SearchJobState.Queued,
                CreatedAt = t,
                LastRequestedAt = t,
            });
            db.SaveChanges();
        }

        using var read = NewDb();
        var job = read.DownloadJobs.Include(j => j.Files).Single();
        job.Id.ShouldBe(id);
        job.State.ShouldBe(DownloadState.Downloading);
        job.PlaceInQueue.ShouldBe(3);
        job.LastProgressAt.UtcTicks.ShouldBe(t.UtcTicks);
        job.CompletedAt.ShouldBeNull();
        job.Files.Single().SlskdTransferId.ShouldBe(transfer);
        job.FailMessage.ShouldBe("oops");
        var search = read.SearchJobs.Single();
        search.State.ShouldBe(SearchJobState.Queued);
        search.SlskdSearchId.ShouldBeNull();
    }

    [Fact]
    public void Migration_wraps_existing_rows_in_one_file()
    {
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<SlskarrDb>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "slskarr.db")};Pooling=False")
            .Options;
        var batch = Guid.NewGuid();
        var transfer = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var ticks = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).UtcTicks;
        using (var old = new SlskarrDb(options))
        {
            old.GetService<IMigrator>().Migrate("20261114132044_AddBatchId");
            old.Database.ExecuteSql($"""
                INSERT INTO Hits (Id, SearchKey, Profile, Username, RemotePath, Size, ReleaseTitle, Format, FreeSlot, QueueLength, UploadSpeed, Rank, SeenAt)
                VALUES ('h1', 'k', 'ebook', 'peer', '@@x\a.epub', 1234, 'T', 'epub', 1, 0, 0, 1, {ticks})
                """);
            old.Database.ExecuteSql($"""
                INSERT INTO DownloadJobs (Id, BatchId, HitId, Category, Title, Username, RemotePath, Size, State, BytesDone, PlaceInQueue, LastProgressAt, CreatedAt, SlskdTransferId)
                VALUES ({jobId}, {batch}, 'h1', 'ebooks', 'T', 'peer', '@@x\a.epub', 1234, 'Downloading', 40, 3, {ticks}, {ticks}, {transfer})
                """);
        }

        using var db = NewDb();
        var hit = db.Hits.Include(h => h.Files).Single();
        var hitFile = hit.Files.ShouldHaveSingleItem();
        hitFile.RemotePath.ShouldBe(@"@@x\a.epub");
        hitFile.Size.ShouldBe(1234);
        hitFile.Subdir.ShouldBe("");

        var job = db.DownloadJobs.Include(j => j.Files).Single();
        job.Id.ShouldBe(jobId);
        var file = job.Files.ShouldHaveSingleItem();
        file.BatchId.ShouldBe(batch);
        file.SlskdTransferId.ShouldBe(transfer);
        file.BytesDone.ShouldBe(40);
        file.State.ShouldBe(DownloadState.Downloading);
        file.PlaceInQueue.ShouldBe(3);
        file.RemotePath.ShouldBe(@"@@x\a.epub");
        file.Subdir.ShouldBe("");
    }

    [Fact]
    public async Task Factory_boots_with_migrated_db_at_data_path()
    {
        using var factory = new SlskarrFactory();
        using var client = factory.CreateClient();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();

        var dataPath = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Slskarr.Configuration.SlskarrOptions>>().Value.DataPath;
        File.Exists(Path.Combine(dataPath, "slskarr.db")).ShouldBeTrue();

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SlskarrDb>().Hits.Count().ShouldBe(0);
        await using var viaFactory = await factory.Services.GetRequiredService<IDbContextFactory<SlskarrDb>>().CreateDbContextAsync();
        viaFactory.Database.GetAppliedMigrations().ShouldNotBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
