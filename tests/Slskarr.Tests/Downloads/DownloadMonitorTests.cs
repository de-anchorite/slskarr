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
        _h.Slskd.Cancels.ShouldBe([(_h.Slskd.TransferIdFor(job.Files[0].BatchId), true)]);
    }

    [Fact]
    public async Task Download_bytes_increase_is_progress_and_sets_downloading()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "InProgress", 100);
        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Downloading);

        _h.Time.Advance(TimeSpan.FromMinutes(50));
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "InProgress", 200);
        await _h.Poll();
        _h.Time.Advance(TimeSpan.FromMinutes(50));
        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Downloading);
        after.BytesDone.ShouldBe(200);
        after.Files[0].SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(job.Files[0].BatchId));
    }

    [Fact]
    public async Task In_progress_back_to_queued_is_queued()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "InProgress", 100);
        await _h.Poll();

        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Queued, Remotely");
        await _h.Poll();

        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);
    }

    [Fact]
    public async Task Rejected_maps_to_failed_with_reason()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Completed, Rejected");

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
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Completed, Succeeded", DownloadHarness.FileSize);

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
        _h.Slskd.Cancels.ShouldBe([(_h.Slskd.TransferIdFor(job.Files[0].BatchId), true)]);
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
        _h.Slskd.SetTransfer(retried.Files[0].BatchId, "Completed, Succeeded", DownloadHarness.FileSize);

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
        after.Files[0].SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(id));
    }

    [Fact]
    public async Task Job_without_transfer_fails_only_after_add_grace()
    {
        var job = await _h.AddJob();
        _h.UpdateJob(job.Id, j => j.Files[0].SlskdTransferId = null);
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
            j.Files[0].SlskdTransferId = null; // a retry whose enqueue left an empty batch
            j.LastProgressAt = retriedAt;
            j.EnqueuedAt = retriedAt;
        });
        _h.Slskd.Batches[job.Files[0].BatchId] = new SlskdBatch(job.Files[0].BatchId, "peer", []);

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

    [Fact]
    public async Task Album_completes_when_all_files_succeed()
    {
        var id = _h.AddAlbum("01.flac", "02.flac", "d2/01.flac");
        _h.SetAlbumFile("01.flac", "Completed, Succeeded", DownloadHarness.FileSize);
        _h.SetAlbumFile("02.flac", "Completed, Succeeded", DownloadHarness.FileSize);
        await _h.Poll();
        _h.Job(id).State.ShouldBe(DownloadState.Queued);

        _h.SetAlbumFile("d2/01.flac", "Completed, Succeeded", DownloadHarness.FileSize);
        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Completed);
        after.CompletedAt.ShouldBe(DownloadHarness.Start);
        after.BytesDone.ShouldBe(DownloadHarness.FileSize * 3);
        after.StoragePath.ShouldBe($"/downloads/slskarr/{id:N}");
        after.Files.ShouldAllBe(f => f.State == DownloadState.Completed);
    }

    [Fact]
    public async Task One_failed_track_fails_album_and_cancels_rest()
    {
        var id = _h.AddAlbum("01.flac", "02.flac", "d2/01.flac");
        var other = _h.Job(id).Files.Where(f => f.RemotePath != DownloadHarness.AlbumPath("02.flac")).Select(f => f.SlskdTransferId!.Value).ToList();
        _h.SetAlbumFile("01.flac", "InProgress", 100);
        _h.SetAlbumFile("02.flac", "Completed, Rejected");

        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldBe("Soulseek: Rejected (02.flac)");
        after.Files.ShouldAllBe(f => f.State == DownloadState.Failed);
        _h.Slskd.Cancels.Select(c => c.TransferId).ShouldBe(other, ignoreOrder: true);
        _h.Slskd.Cancels.ShouldAllBe(c => c.Remove);
    }

    [Fact]
    public async Task Progress_on_any_track_resets_stall_clock()
    {
        var id = _h.AddAlbum("01.flac", "02.flac");
        _h.SetAlbumFile("01.flac", "InProgress", 100);
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromMinutes(50));
        _h.SetAlbumFile("01.flac", "InProgress", 200);
        await _h.Poll();
        _h.Time.Advance(TimeSpan.FromMinutes(50));
        await _h.Poll();
        _h.Job(id).State.ShouldBe(DownloadState.Downloading);
        _h.Slskd.Cancels.ShouldBeEmpty();

        _h.Time.Advance(TimeSpan.FromMinutes(11));
        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldStartWith("Stalled");
        _h.Slskd.Cancels.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Queue_position_is_minimum_over_batches()
    {
        var id = _h.AddAlbum("01.flac", "02.flac", "03.flac", "d2/01.flac", "d2/02.flac");
        _h.SetAlbumFile("01.flac", "InProgress", 10);
        var d2 = TransferOf(id, "d2/01.flac");
        _h.Slskd.PlaceFor = (_, t) => t == d2 ? 3 : 7;

        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Downloading);
        after.PlaceInQueue.ShouldBe(3);
        after.Files.Where(f => f.State == DownloadState.Downloading).ShouldAllBe(f => f.PlaceInQueue == null);
    }

    private Guid TransferOf(Guid job, string name) =>
        _h.Job(job).Files.Single(f => f.RemotePath == DownloadHarness.AlbumPath(name)).SlskdTransferId!.Value;

    // A 12-track album must not cost 12 peer requests every poll: one queued file per batch is asked.
    [Fact]
    public async Task Queued_album_asks_one_queue_position_per_batch_per_poll()
    {
        var names = Enumerable.Range(1, 6).SelectMany(i => new[] { $"{i:00}.flac", $"d2/{i:00}.flac" }).ToArray();
        var id = _h.AddAlbum(names);
        _h.Slskd.Place = 5;

        await _h.Poll();
        _h.Slskd.PlaceCalls.ShouldBe([TransferOf(id, "01.flac"), TransferOf(id, "d2/01.flac")], ignoreOrder: true);
        _h.Job(id).PlaceInQueue.ShouldBe(5);

        _h.Slskd.PlaceCalls.Clear();
        _h.SetAlbumFile("01.flac", "InProgress", 10);
        await _h.Poll();
        _h.Slskd.PlaceCalls.ShouldBe([TransferOf(id, "02.flac"), TransferOf(id, "d2/01.flac")], ignoreOrder: true);
    }

    [Fact]
    public async Task Queued_album_position_change_resets_stall_clock()
    {
        var id = _h.AddAlbum([.. Enumerable.Range(1, 12).Select(i => $"{i:00}.flac")]);
        _h.Slskd.Place = 5;
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromMinutes(59));
        _h.Slskd.Place = 4;
        await _h.Poll();
        _h.Job(id).PlaceInQueue.ShouldBe(4);

        _h.Time.Advance(TimeSpan.FromMinutes(6));
        await _h.Poll();
        _h.Job(id).State.ShouldBe(DownloadState.Queued);
        _h.Slskd.Cancels.ShouldBeEmpty();
        _h.Slskd.PlaceCalls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Missing_batch_completes_only_if_all_files_on_disk()
    {
        var id = _h.AddAlbum("01.flac", "d2/01.flac");
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        _h.Slskd.Batches.Clear();
        _h.Slskd.Dirs[$"slskarr/{id:N}"] = [new SlskdLocalFile("01.flac", DownloadHarness.FileSize)];

        await _h.Poll();

        var failed = _h.Job(id);
        failed.State.ShouldBe(DownloadState.Failed);
        failed.FailMessage.ShouldBe("Transfer disappeared from slskd");

        var ok = _h.AddAlbum("01.flac", "d2/01.flac");
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        _h.Slskd.Batches.Clear();
        _h.Slskd.Dirs[$"slskarr/{ok:N}"] = [new SlskdLocalFile("01.flac", DownloadHarness.FileSize)];
        _h.Slskd.Dirs[$"slskarr/{ok:N}/d2"] = [new SlskdLocalFile("01.flac", DownloadHarness.FileSize)];

        await _h.Poll();

        var done = _h.Job(ok);
        done.State.ShouldBe(DownloadState.Completed);
        done.StoragePath.ShouldBe($"/downloads/slskarr/{ok:N}");
    }

    [Fact]
    public async Task Migrated_ebook_job_still_completes()
    {
        // Shape of a job after the AddJobFiles migration: one file, the job's own batch and transfer ids.
        var job = await _h.AddJob();
        var file = job.Files.ShouldHaveSingleItem();
        file.Subdir.ShouldBe("");
        _h.Slskd.SetTransfer(file.BatchId, "Completed, Succeeded", DownloadHarness.FileSize);

        await _h.Poll();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Completed);
        after.StoragePath.ShouldBe($"/downloads/slskarr/{job.Id:N}/Frank Herbert - Dune (1965).epub");
    }

    // Storage shape comes from the job's files, not its SAB category (which the user can change in the *arr).
    [Fact]
    public async Task Album_in_ebooks_category_is_stored_as_folder()
    {
        var id = _h.AddAlbum("01.flac", "02.flac");
        _h.UpdateJob(id, j => j.Category = "ebooks");
        _h.SetAlbumFile("01.flac", "Completed, Succeeded", DownloadHarness.FileSize);
        _h.SetAlbumFile("02.flac", "Completed, Succeeded", DownloadHarness.FileSize);

        await _h.Poll();

        _h.Job(id).StoragePath.ShouldBe($"/downloads/slskarr/{id:N}");
    }

    [Fact]
    public async Task Single_track_album_is_stored_as_folder()
    {
        var id = _h.AddAlbum("01.flac");
        _h.SetAlbumFile("01.flac", "Completed, Succeeded", DownloadHarness.FileSize);

        await _h.Poll();

        _h.Job(id).StoragePath.ShouldBe($"/downloads/slskarr/{id:N}");
    }

    [Fact]
    public async Task Ebook_in_music_category_is_stored_as_file()
    {
        var job = await _h.AddJob();
        _h.UpdateJob(job.Id, j => j.Category = DownloadHarness.MusicCategory);
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Completed, Succeeded", DownloadHarness.FileSize);

        await _h.Poll();

        _h.Job(job.Id).StoragePath.ShouldBe($"/downloads/slskarr/{job.Id:N}/Frank Herbert - Dune (1965).epub");
    }

    [Fact]
    public async Task Stall_with_cancel_failing_keeps_job_active_until_removal_works()
    {
        var id = _h.AddAlbum("01.flac", "02.flac");
        _h.Slskd.Place = 5;
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromMinutes(61));
        _h.Slskd.CancelThrows = true;
        await _h.Poll();
        _h.Job(id).State.ShouldBe(DownloadState.Queued);
        _h.Slskd.Cancels.ShouldBeEmpty();

        _h.Slskd.CancelThrows = false;
        await _h.Poll();
        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldStartWith("Stalled");
        _h.Slskd.Cancels.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Failed_track_with_cancel_failing_keeps_job_active_until_removal_works()
    {
        var id = _h.AddAlbum("01.flac", "02.flac");
        _h.SetAlbumFile("01.flac", "InProgress", 100);
        _h.SetAlbumFile("02.flac", "Completed, Rejected");
        _h.Slskd.CancelThrows = true;
        await _h.Poll();
        _h.Job(id).State.ShouldBe(DownloadState.Queued); // nothing was saved

        _h.Slskd.CancelThrows = false;
        await _h.Poll();
        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldBe("Soulseek: Rejected (02.flac)");
        _h.Slskd.Cancels.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Batch_that_never_appears_fails_after_grace_despite_progress_elsewhere()
    {
        var id = _h.AddAlbum("01.flac", "d2/01.flac");
        var disc2 = _h.Job(id).Files.Single(f => f.Subdir == "d2").BatchId;
        _h.UpdateJob(id, j => j.Files.Single(f => f.Subdir == "d2").SlskdTransferId = null);
        _h.Slskd.Batches[disc2] = new SlskdBatch(disc2, "peer", []);

        for (var minute = 1; minute <= 2; minute++)
        {
            _h.Time.Advance(TimeSpan.FromMinutes(1));
            _h.SetAlbumFile("01.flac", "InProgress", minute * 100);
            await _h.Poll();
            _h.Job(id).State.ShouldBe(DownloadState.Downloading);
        }

        _h.Time.Advance(TimeSpan.FromMinutes(1.5));
        _h.SetAlbumFile("01.flac", "InProgress", 300);
        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldBe("Transfer disappeared from slskd");
    }

    [Fact]
    public async Task Missing_batch_beside_a_progressing_one_is_resolved_from_its_own_folder()
    {
        var id = _h.AddAlbum("01.flac", "d2/01.flac");
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        _h.Slskd.Batches.Remove(_h.Job(id).Files.Single(f => f.Subdir == "d2").BatchId);
        _h.SetAlbumFile("01.flac", "InProgress", 100);
        _h.Slskd.Dirs[$"slskarr/{id:N}/d2"] = [new SlskdLocalFile("01.flac", DownloadHarness.FileSize)];

        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Downloading);
        after.Files.Single(f => f.Subdir == "d2").State.ShouldBe(DownloadState.Completed);
        after.Files.Single(f => f.Subdir == "").State.ShouldBe(DownloadState.Downloading);
    }

    [Fact]
    public async Task Missing_file_with_wrong_size_on_disk_fails()
    {
        var id = _h.AddAlbum("01.flac", "02.flac");
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        _h.Slskd.Batches.Clear();
        _h.Slskd.Dirs[$"slskarr/{id:N}"] = [new SlskdLocalFile("01.flac", DownloadHarness.FileSize), new SlskdLocalFile("02.flac", 10)];

        await _h.Poll();

        var after = _h.Job(id);
        after.State.ShouldBe(DownloadState.Failed);
        after.FailMessage.ShouldBe("Transfer disappeared from slskd");
    }

    [Fact]
    public async Task Slskd_outage_is_not_counted_toward_stall_for_multi_batch_jobs()
    {
        var id = _h.AddAlbum("01.flac", "d2/01.flac");
        _h.Slskd.Place = 5;
        await _h.Poll();

        _h.Slskd.Unavailable = true;
        _h.Time.Advance(TimeSpan.FromMinutes(50));
        await _h.Poll();
        _h.Time.Advance(TimeSpan.FromMinutes(50));
        await _h.Poll();
        _h.Slskd.Unavailable = false;
        await _h.Poll();

        _h.Job(id).State.ShouldBe(DownloadState.Queued);
        _h.Slskd.Cancels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_batch_is_asked_for_once_per_poll()
    {
        var id = _h.AddAlbum("01.flac", "02.flac", "d2/01.flac");
        var batches = _h.Job(id).Files.Select(f => f.BatchId).Distinct().ToList();
        batches.Count.ShouldBe(2);

        await _h.Poll();

        _h.Slskd.BatchCalls.ShouldBe(batches, ignoreOrder: true);
    }

    [Fact]
    public async Task Progress_on_a_different_track_than_before_resets_stall_clock()
    {
        var id = _h.AddAlbum("01.flac", "02.flac");
        _h.SetAlbumFile("01.flac", "InProgress", 100);
        _h.SetAlbumFile("02.flac", "InProgress", 50);
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromMinutes(50));
        _h.SetAlbumFile("02.flac", "InProgress", 100);
        await _h.Poll();

        _h.Time.Advance(TimeSpan.FromMinutes(50));
        await _h.Poll();
        _h.Job(id).State.ShouldBe(DownloadState.Downloading);

        _h.Time.Advance(TimeSpan.FromMinutes(11));
        await _h.Poll();
        _h.Job(id).State.ShouldBe(DownloadState.Failed);
    }

    [Fact]
    public async Task Single_file_job_uses_the_only_transfer_of_its_batch_under_another_name()
    {
        var job = await _h.AddJob();
        var batch = job.Files[0].BatchId;
        _h.Slskd.Batches[batch] = new SlskdBatch(batch, "peer",
            [new SlskdTransfer(Guid.NewGuid(), batch, "peer", @"@@abcde\Other\name.epub", DownloadHarness.FileSize, "Completed, Succeeded", DownloadHarness.FileSize, 0, null, null)]);

        await _h.Poll();

        _h.Job(job.Id).State.ShouldBe(DownloadState.Completed);
    }
}
