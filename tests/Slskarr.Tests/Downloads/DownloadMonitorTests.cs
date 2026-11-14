using Shouldly;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Downloads;

public sealed class DownloadMonitorTests : IDisposable
{
    private DownloadHarness _h = new();

    public void Dispose() => _h.Dispose();

    private DownloadHarness Use(Action<SlskarrOptions> configure)
    {
        _h.Dispose();
        return _h = new DownloadHarness(configure);
    }

    [Fact]
    public async Task Queue_position_moving_is_progress()
    {
        var job = await _h.AddJob();
        _h.Slskd.Place = 5;
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromMinutes(59));
        _h.Slskd.Place = 4;
        await _h.Poll();
        _h.Job(job.Id).PlaceInQueue.ShouldBe(4);

        _h.Time.Advance(TimeSpan.FromMinutes(6));
        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Queued);
        _h.Slskd.Cancels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stuck_queue_fails_after_stall_timeout()
    {
        var job = await _h.AddJob();
        _h.Slskd.Place = 5;
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromMinutes(61));
        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldStartWith("Stalled");
        _h.Slskd.Cancels.ShouldBe([(_h.Slskd.TransferIdFor(job.BatchId), true)]);
    }

    [Fact]
    public async Task Download_bytes_increase_is_progress_and_sets_downloading()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.BatchId, "InProgress", 100);
        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Downloading);

        _h.Time.Advance(TimeSpan.FromMinutes(50));
        _h.Slskd.SetTransfer(job.BatchId, "InProgress", 200);
        await _h.Poll();
        _h.Time.Advance(TimeSpan.FromMinutes(50));
        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Downloading);
        after.BytesDone.ShouldBe(200);
        after.SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(job.BatchId));
    }

    [Fact]
    public async Task In_progress_back_to_queued_is_queued()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.BatchId, "InProgress", 100);
        await _h.Poll();

        _h.Slskd.SetTransfer(job.BatchId, "Queued, Remotely");
        await _h.Poll();

        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);
    }

    [Fact]
    public async Task Rejected_maps_to_failed_with_reason()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.BatchId, "Completed, Rejected");

        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldBe("Soulseek: Rejected");
    }

    [Fact]
    public async Task Succeeded_sets_storage_path()
    {
        Use(o =>
        {
            o.Slskd.DownloadsPath = "/downloads";
            o.Paths.StorageRewriteFrom = "/downloads";
            o.Paths.StorageRewriteTo = "/books";
        });
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.BatchId, "Completed, Succeeded", DownloadHarness.FileSize);

        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Completed);
        after.CompletedAt.ShouldBe(DownloadHarness.Start);
        after.StoragePath.ShouldBe($"/books/slskarr/{job.Id:N}/Frank Herbert - Dune (1965).epub");
    }

    [Fact]
    public async Task Monitor_keeps_state_when_slskd_unreachable()
    {
        var job = await _h.AddJob();
        _h.Slskd.Place = 3;
        await _h.Poll();
        var before = _h.Job(job.Id);

        _h.Slskd.Unavailable = true;
        _h.Time.Advance(TimeSpan.FromHours(2));
        await _h.Poll();
        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Queued);
        after.LastProgressAt.ShouldBe(before.LastProgressAt);
        after.PlaceInQueue.ShouldBe(3);
        after.FailMessage.ShouldBeNull();
        _h.Slskd.Cancels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Batch_gone_with_full_size_file_is_completed()
    {
        var job = await _h.AddJob();
        _h.Slskd.Batches.Clear();
        _h.Slskd.Dirs[$"slskarr/{job.Id:N}"] = [new SlskdLocalFile("Frank Herbert - Dune (1965).epub", DownloadHarness.FileSize)];

        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Completed);
        after.StoragePath.ShouldBe($"/downloads/slskarr/{job.Id:N}/Frank Herbert - Dune (1965).epub");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_gone_without_full_file_is_failed(bool partialFile)
    {
        var job = await _h.AddJob();
        _h.Slskd.Batches.Clear();
        if (partialFile)
        {
            _h.Slskd.Dirs[$"slskarr/{job.Id:N}"] = [new SlskdLocalFile("Frank Herbert - Dune (1965).epub", 10)];
        }

        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldBe("Transfer disappeared from slskd");
    }

    [Fact]
    public async Task Retry_monitors_new_batch()
    {
        _h.Slskd.EnqueueError = "peer offline";
        var job = await _h.AddJob();
        _h.Slskd.EnqueueError = null;
        await _h.Manager.RetryAsync(Slskarr.Downloads.DownloadManager.NzoId(job.Id), CancellationToken.None);
        var retried = _h.Job(job.Id);
        _h.Slskd.SetTransfer(retried.BatchId, "Completed, Succeeded", DownloadHarness.FileSize);

        await _h.Poll();

        _h.Job(job.Id).State.ShouldBe(DownloadState.Completed);
    }
}
