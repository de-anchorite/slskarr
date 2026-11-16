using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Downloads;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Downloads;

/// <summary>Manager + monitor over a temp SQLite db, a <see cref="FakeTimeProvider"/> and a <see cref="FakeDownloadSlskd"/>.</summary>
public sealed class DownloadHarness : IDisposable
{
    public const string RemotePath = @"@@abcde\Books\Frank Herbert\Frank Herbert - Dune (1965).epub";
    public const string HitId = "hit-dune";
    public const long FileSize = 1_000_000;

    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slskarr-download-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;

    public DownloadHarness(Action<SlskarrOptions>? configure = null)
    {
        Directory.CreateDirectory(_dir);
        var options = new SlskarrOptions();
        configure?.Invoke(options);
        Options = Microsoft.Extensions.Options.Options.Create(options);
        _services = new ServiceCollection()
            .AddDbContextFactory<SlskarrDb>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "slskarr.db")};Pooling=False"))
            .BuildServiceProvider();
        Db = _services.GetRequiredService<IDbContextFactory<SlskarrDb>>();
        using (var db = Db.CreateDbContext())
        {
            db.Database.Migrate();
            db.Hits.Add(NewHit(HitId));
            db.SaveChanges();
        }

        Slskd = new FakeDownloadSlskd();
        Status = new FileManagementStatus();
        Manager = new DownloadManager(Db, Slskd, Time, Status, NullLogger<DownloadManager>.Instance);
        Monitor = new DownloadMonitor(Db, Slskd, Options, Time, NullLogger<DownloadMonitor>.Instance);
    }

    public FakeTimeProvider Time { get; } = new(Start);

    public IOptions<SlskarrOptions> Options { get; }

    public IDbContextFactory<SlskarrDb> Db { get; }

    public FakeDownloadSlskd Slskd { get; }

    public FileManagementStatus Status { get; }

    public DownloadManager Manager { get; }

    public DownloadMonitor Monitor { get; private set; }

    /// <summary>Simulates a process restart for the monitor: a new instance, constructed at the current fake time.</summary>
    public void RestartMonitor() =>
        Monitor = new DownloadMonitor(Db, Slskd, Options, Time, NullLogger<DownloadMonitor>.Instance);

    public const string IncompleteFile = "peer/Books/Frank Herbert/Frank Herbert - Dune (1965).epub";

    public static HitEntity NewHit(string id, string searchKey = "k") => new()
    {
        Id = id,
        SearchKey = searchKey,
        Profile = "ebook",
        Username = "peer",
        RemotePath = RemotePath,
        Size = FileSize,
        ReleaseTitle = "Frank Herbert - Dune (1965)",
        Format = "EPUB",
        Files = [new HitFileEntity { HitId = id, RemotePath = RemotePath, Size = FileSize }],
    };

    public async Task<DownloadJobEntity> AddJob()
    {
        var result = await Manager.AddAsync(HitId, "ebooks", CancellationToken.None);
        return Job(DownloadManager.ParseNzoId(result.NzoId!)!.Value);
    }

    public DownloadJobEntity Job(Guid id)
    {
        using var db = Db.CreateDbContext();
        return db.DownloadJobs.AsNoTracking().Include(j => j.Files).Single(j => j.Id == id);
    }

    /// <summary>Changes a stored job directly, e.g. to set up a state the manager would only reach in a race.</summary>
    public void UpdateJob(Guid id, Action<DownloadJobEntity> change)
    {
        using var db = Db.CreateDbContext();
        change(db.DownloadJobs.Include(j => j.Files).Single(j => j.Id == id));
        db.SaveChanges();
    }

    public Task Poll() => Monitor.PollOnceAsync(CancellationToken.None);

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public sealed class FakeDownloadSlskd : ISlskdClient
{
    public record EnqueueCall(Guid BatchId, string Username, IReadOnlyList<SlskdEnqueueFile> Files, string Destination)
    {
        public string RemotePath => Files[0].Filename;

        public long Size => Files[0].Size;
    }

    public List<EnqueueCall> Enqueues { get; } = [];

    public Dictionary<Guid, SlskdBatch> Batches { get; } = [];

    public List<(Guid TransferId, bool Remove)> Cancels { get; } = [];

    public List<string> DeletedDownloadDirs { get; } = [];

    public List<string> DeletedIncompleteFiles { get; } = [];

    /// <summary>Cancels and file deletes, in call order.</summary>
    public List<string> Calls { get; } = [];

    public Dictionary<string, IReadOnlyList<SlskdLocalFile>?> Dirs { get; } = [];

    public bool Unavailable { get; set; }

    public bool PlaceThrows { get; set; }

    public int? Place { get; set; }

    public string? EnqueueError { get; set; }

    public bool EnqueueThrows { get; set; }

    /// <summary>1-based number of the one enqueue call that throws <see cref="SlskdUnavailableException"/>.</summary>
    public int? ThrowEnqueueNumber { get; set; }

    /// <summary>1-based number of the one enqueue call that throws a non-slskd exception.</summary>
    public int? ThrowOtherEnqueueNumber { get; set; }

    public bool CancelThrows { get; set; }

    /// <summary>1-based number of the one enqueue call that is rejected with <see cref="EnqueueError"/>-style failure.</summary>
    public int? FailEnqueueNumber { get; set; }

    public SlskdDeleteResult DeleteResult { get; set; } = SlskdDeleteResult.Deleted;

    /// <summary>Holds the enqueue (until released or cancelled), as slskd does while it connects to the peer.</summary>
    public TaskCompletionSource? EnqueueGate { get; set; }

    /// <summary>
    /// When set, slskd has already created the batch with this many transfers (0 or 1) before the enqueue
    /// waits at <see cref="EnqueueGate"/> or fails: what slskd leaves behind when the call breaks off.
    /// </summary>
    public int? BatchBeforeFailure { get; set; }

    public Guid TransferIdFor(Guid batchId) => Batches[batchId].Transfers[0].Id;

    /// <summary>Replaces the batch's single transfer with the given state.</summary>
    public void SetTransfer(Guid batchId, string state, long bytes = 0)
    {
        var id = Batches.TryGetValue(batchId, out var b) ? b.Transfers[0].Id : Guid.NewGuid();
        Batches[batchId] = Batch(batchId, id, state, bytes);
    }

    private static SlskdBatch Batch(Guid batchId, Guid transferId, string state, long bytes) =>
        new(batchId, "peer",
        [
            new SlskdTransfer(transferId, batchId, "peer", DownloadHarness.RemotePath, DownloadHarness.FileSize,
                state, bytes, 0, null, null),
        ]);

    private static SlskdBatch BatchOf(Guid batchId, IReadOnlyList<SlskdEnqueueFile> files) =>
        new(batchId, "peer",
            [.. files.Select(f => new SlskdTransfer(Guid.NewGuid(), batchId, "peer", f.Filename, f.Size, "Queued, Remotely", 0, 0, null, null))]);

    public async Task<SlskdEnqueueResult> EnqueueBatchAsync(
        Guid batchId, string username, IReadOnlyList<SlskdEnqueueFile> files, string destination, CancellationToken ct)
    {
        Enqueues.Add(new EnqueueCall(batchId, username, [.. files], destination));
        if (BatchBeforeFailure is { } transfers && (ThrowEnqueueNumber is null || ThrowEnqueueNumber == Enqueues.Count))
        {
            Batches[batchId] = transfers == 0 ? new SlskdBatch(batchId, "peer", []) : BatchOf(batchId, files);
        }

        if (EnqueueGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (ThrowOtherEnqueueNumber == Enqueues.Count)
        {
            throw new InvalidOperationException("boom");
        }

        if (EnqueueThrows || ThrowEnqueueNumber == Enqueues.Count)
        {
            throw new SlskdUnavailableException("down");
        }

        if (FailEnqueueNumber == Enqueues.Count)
        {
            return new SlskdEnqueueResult(false, null, "rejected");
        }

        if (EnqueueError is { } error)
        {
            return new SlskdEnqueueResult(false, null, error);
        }

        if (!Batches.TryGetValue(batchId, out var batch) || batch.Transfers.Count == 0)
        {
            batch = BatchOf(batchId, files);
            Batches[batchId] = batch;
        }

        return new SlskdEnqueueResult(true, batch, null);
    }

    public Task<SlskdBatch?> GetBatchAsync(Guid batchId, CancellationToken ct)
    {
        if (Unavailable)
        {
            throw new SlskdUnavailableException("down");
        }

        return Task.FromResult(Batches.GetValueOrDefault(batchId));
    }

    public Task<int?> GetPlaceInQueueAsync(string username, Guid transferId, CancellationToken ct)
    {
        if (Unavailable || PlaceThrows)
        {
            throw new SlskdUnavailableException("down");
        }

        return Task.FromResult(Place);
    }

    public Task CancelDownloadAsync(string username, Guid transferId, bool remove, CancellationToken ct)
    {
        if (CancelThrows)
        {
            throw new SlskdUnavailableException("down");
        }

        Cancels.Add((transferId, remove));
        Calls.Add("cancel");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SlskdLocalFile>?> ListDownloadDirAsync(string relativeDir, CancellationToken ct) =>
        Task.FromResult(Dirs.GetValueOrDefault(relativeDir));

    public Task<SlskdDeleteResult> DeleteDownloadDirAsync(string relativeDir, CancellationToken ct)
    {
        DeletedDownloadDirs.Add(relativeDir);
        Calls.Add("delete-download-dir");
        return Task.FromResult(DeleteResult);
    }

    public Task<SlskdDeleteResult> DeleteIncompleteFileAsync(string relativePath, CancellationToken ct)
    {
        DeletedIncompleteFiles.Add(relativePath);
        Calls.Add("delete-incomplete-file");
        return Task.FromResult(DeleteResult);
    }

    public Task<Guid> StartSearchAsync(string text, int timeoutSeconds, int responseLimit, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<SlskdSearch?> GetSearchAsync(Guid id, bool includeResponses, CancellationToken ct) => throw new NotSupportedException();

    public Task DeleteSearchAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetVersionAsync(CancellationToken ct) => throw new NotSupportedException();
}
