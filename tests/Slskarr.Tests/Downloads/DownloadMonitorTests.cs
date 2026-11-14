using Shouldly;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Downloads;
using Slskarr.Soulseek;
using Slskarr.Tests.Search;

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
    public async Task Offline_peer_queue_position_error_still_stalls()
    {
        var job = await _h.AddJob();
        _h.Slskd.PlaceThrows = true;
        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);

        _h.Time.Advance(TimeSpan.FromMinutes(61));
        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldStartWith("Stalled");
        _h.Slskd.Cancels.ShouldBe([(_h.Slskd.TransferIdFor(job.BatchId), true)]);
    }

    [Fact]
    public async Task Slskd_outage_is_not_counted_toward_stall()
    {
        var job = await _h.AddJob();
        _h.Slskd.Place = 3;
        await _h.Poll();

        _h.Slskd.Unavailable = true;
        await _h.Poll();
        _h.Time.Advance(TimeSpan.FromHours(2));
        await _h.Poll();
        _h.Slskd.Unavailable = false;
        await _h.Poll();

        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);

        _h.Time.Advance(TimeSpan.FromMinutes(61));
        await _h.Poll();

        _h.Job(job.Id).FailMessage.ShouldStartWith("Stalled");
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

    [Fact]
    public async Task Poll_during_in_flight_enqueue_does_not_fail_the_job()
    {
        var gate = _h.Slskd.EnqueueGate = new TaskCompletionSource();
        var add = _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);
        await SearchHarness.Until(() => _h.Slskd.Enqueues.Count == 1, "the enqueue is in flight");
        var id = _h.Slskd.Enqueues[0].BatchId;

        _h.Time.Advance(TimeSpan.FromSeconds(30));
        await _h.Poll();

        var during = _h.Job(id);
        during.State.ShouldBe(DownloadState.Queued);
        during.FailMessage.ShouldBeNull();

        gate.SetResult();
        (await add.Within()).Ok.ShouldBeTrue();
        _h.Slskd.SetTransfer(id, "InProgress", 100);
        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Downloading);
        after.SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(id));
    }

    [Fact]
    public async Task Job_without_transfer_fails_only_after_add_grace()
    {
        var job = await _h.AddJob();
        _h.UpdateJob(job.Id, j => j.SlskdTransferId = null);
        _h.Slskd.Batches.Clear();

        _h.Time.Advance(DownloadMonitor.AddGrace - TimeSpan.FromSeconds(1));
        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);

        _h.Time.Advance(TimeSpan.FromSeconds(2));
        await _h.Poll();
        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldBe("Transfer disappeared from slskd");
    }

    [Fact]
    public async Task Add_grace_counts_from_the_latest_enqueue_not_creation()
    {
        var job = await _h.AddJob();
        _h.Time.Advance(TimeSpan.FromHours(3));
        var retriedAt = _h.Time.GetUtcNow();
        _h.UpdateJob(job.Id, j =>
        {
            j.SlskdTransferId = null; // a retry whose enqueue left an empty batch
            j.LastProgressAt = retriedAt;
        });
        _h.Slskd.Batches[job.BatchId] = new SlskdBatch(job.BatchId, "peer", []);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await _h.Poll();

        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);
    }

    [Fact]
    public async Task Downtime_before_a_restart_is_not_counted_toward_stall()
    {
        var job = await _h.AddJob();
        _h.Slskd.Place = 5;
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromHours(2)); // Slskarr was down
        _h.RestartMonitor();
        var restartedAt = _h.Time.GetUtcNow();
        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Queued);
        after.LastProgressAt.ShouldBe(restartedAt);
        _h.Slskd.Cancels.ShouldBeEmpty();

        _h.Time.Advance(TimeSpan.FromMinutes(61));
        await _h.Poll();

        _h.Job(job.Id).FailMessage.ShouldStartWith("Stalled");
    }

    [Fact]
    public async Task Restart_never_moves_the_stall_clock_back()
    {
        var job = await _h.AddJob();
        _h.Slskd.Place = 5;
        _h.RestartMonitor();
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        var progressAt = _h.Time.GetUtcNow();
        _h.UpdateJob(job.Id, j => j.LastProgressAt = progressAt);

        await _h.Poll();

        _h.Job(job.Id).LastProgressAt.ShouldBe(progressAt);
    }

    [Fact]
    public async Task Restart_adjustment_survives_a_first_poll_that_finds_slskd_down()
    {
        var job = await _h.AddJob();
        _h.Slskd.Place = 5;
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromHours(2));
        _h.RestartMonitor();
        _h.Slskd.Unavailable = true;
        await _h.Poll();
        _h.Slskd.Unavailable = false;
        await _h.Poll();

        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);
    }
}
