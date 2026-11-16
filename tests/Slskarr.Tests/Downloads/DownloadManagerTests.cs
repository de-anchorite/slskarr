using Microsoft.EntityFrameworkCore;
using Shouldly;
using Slskarr.Data;
using Slskarr.Downloads;
using Slskarr.Soulseek;
using Slskarr.Tests.Search;

namespace Slskarr.Tests.Downloads;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly DownloadHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Add_creates_job_and_enqueues_with_destination()
    {
        var result = await _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);

        result.Ok.ShouldBeTrue();
        var job = _h.Job(DownloadManager.ParseNzoId(result.NzoId!)!.Value);
        result.NzoId.ShouldBe("SLSKARR_" + job.Id.ToString("N"));
        job.State.ShouldBe(DownloadState.Queued);
        job.Files[0].BatchId.ShouldBe(job.Id);
        job.LastProgressAt.ShouldBe(DownloadHarness.Start);
        job.Category.ShouldBe("ebooks");
        job.Title.ShouldBe("Frank Herbert - Dune (1965)");
        var call = _h.Slskd.Enqueues.Single();
        call.BatchId.ShouldBe(job.Id);
        call.Username.ShouldBe("peer");
        call.RemotePath.ShouldBe(DownloadHarness.RemotePath);
        call.Size.ShouldBe(DownloadHarness.FileSize);
        call.Destination.ShouldBe("slskarr/" + job.Id.ToString("N"));
        (await _h.Manager.ActiveAsync(null, CancellationToken.None)).Select(j => j.Id).ShouldBe([job.Id]);
        (await _h.Manager.ActiveAsync("other", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_same_hit_twice_returns_existing_job()
    {
        var first = await _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);
        var second = await _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);

        second.NzoId.ShouldBe(first.NzoId);
        _h.Slskd.Enqueues.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Add_concurrent_duplicates_enqueue_once()
    {
        var gate = _h.Slskd.EnqueueGate = new TaskCompletionSource();
        var a = _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);
        var b = _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);
        await Task.Delay(50);
        gate.SetResult();

        var results = await Task.WhenAll(a, b).Within();

        results[0].NzoId.ShouldBe(results[1].NzoId);
        _h.Slskd.Enqueues.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Add_unknown_hit_fails()
    {
        var result = await _h.Manager.AddAsync("nope", "ebooks", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("Release expired or unknown");
        _h.Slskd.Enqueues.ShouldBeEmpty();
    }

    [Fact]
    public async Task Add_peer_offline_shows_failed_in_history()
    {
        _h.Slskd.EnqueueError = "peer offline";

        var result = await _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);

        result.Ok.ShouldBeTrue();
        var history = await _h.Manager.HistoryAsync("ebooks", CancellationToken.None);
        var job = history.Single();
        result.NzoId.ShouldBe(DownloadManager.NzoId(job.Id));
        job.State.ShouldBe(DownloadState.Failed);
        job.FailMessage.ShouldBe("peer offline");
        (await _h.Manager.ActiveAsync(null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_with_files_calls_slskd_delete_dir()
    {
        var completed = await _h.AddJob();
        _h.Slskd.SetTransfer(completed.Files[0].BatchId, "Completed, Succeeded", DownloadHarness.FileSize);
        await _h.Poll();
        _h.Job(completed.Id).State.ShouldBe(DownloadState.Completed);

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(completed.Id), true, CancellationToken.None)).ShouldBeTrue();

        _h.Slskd.DeletedDownloadDirs.ShouldBe(["slskarr/" + completed.Id.ToString("N")]);
        _h.Slskd.DeletedIncompleteFiles.ShouldBeEmpty();
        _h.Slskd.Cancels.ShouldContain((_h.Slskd.TransferIdFor(completed.Files[0].BatchId), true));
        _h.Job(completed.Id).State.ShouldBe(DownloadState.Deleted);
    }

    [Fact]
    public async Task Delete_failed_with_files_deletes_only_the_partial_file()
    {
        _h.Slskd.EnqueueError = "peer offline";
        var job = await _h.AddJob();

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(job.Id), true, CancellationToken.None)).ShouldBeTrue();

        _h.Slskd.DeletedIncompleteFiles.ShouldBe([DownloadHarness.IncompleteFile]);
        _h.Slskd.DeletedDownloadDirs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_downloading_with_files_cancels_then_deletes_the_partial_file()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "InProgress", 100);
        await _h.Poll();

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(job.Id), true, CancellationToken.None)).ShouldBeTrue();

        _h.Slskd.Calls.ShouldBe(["cancel", "delete-incomplete-file"]);
        _h.Slskd.DeletedIncompleteFiles.ShouldBe([DownloadHarness.IncompleteFile]);
        _h.Job(job.Id).State.ShouldBe(DownloadState.Deleted);
    }

    [Fact]
    public async Task Delete_keeps_a_partial_file_another_live_job_shares()
    {
        await using (var db = await _h.Db.CreateDbContextAsync())
        {
            db.Hits.Add(DownloadHarness.NewHit("hit-dune-other-search", searchKey: "other"));
            await db.SaveChangesAsync();
        }

        var first = await _h.AddJob();
        var second = DownloadManager.ParseNzoId(
            (await _h.Manager.AddAsync("hit-dune-other-search", "ebooks", CancellationToken.None)).NzoId!)!.Value;

        await _h.Manager.DeleteAsync(DownloadManager.NzoId(first.Id), true, CancellationToken.None);
        _h.Slskd.DeletedIncompleteFiles.ShouldBeEmpty();

        await _h.Manager.DeleteAsync(DownloadManager.NzoId(second), true, CancellationToken.None);
        _h.Slskd.DeletedIncompleteFiles.ShouldBe([DownloadHarness.IncompleteFile]);
    }

    [Fact]
    public async Task Delete_without_files_keeps_directories_and_cancels_active_transfer()
    {
        var job = await _h.AddJob();
        await _h.Poll();

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(job.Id), false, CancellationToken.None)).ShouldBeTrue();

        _h.Slskd.DeletedDownloadDirs.ShouldBeEmpty();
        _h.Slskd.DeletedIncompleteFiles.ShouldBeEmpty();
        _h.Slskd.Cancels.ShouldBe([(_h.Slskd.TransferIdFor(job.Files[0].BatchId), true)]);
        (await _h.Manager.ActiveAsync(null, CancellationToken.None)).ShouldBeEmpty();
        (await _h.Manager.HistoryAsync(null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_unknown_returns_false()
    {
        (await _h.Manager.DeleteAsync("SLSKARR_" + Guid.NewGuid().ToString("N"), false, CancellationToken.None)).ShouldBeFalse();
        (await _h.Manager.DeleteAsync("garbage", false, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_forbidden_warns_and_succeeds()
    {
        _h.Slskd.EnqueueError = "peer offline";
        var job = await _h.AddJob();
        _h.Slskd.DeleteResult = SlskdDeleteResult.Forbidden;

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(job.Id), true, CancellationToken.None)).ShouldBeTrue();

        _h.Status.RemoteDeleteForbidden.ShouldBeTrue();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Deleted);
    }

    [Fact]
    public async Task Delete_not_found_counts_as_success()
    {
        _h.Slskd.EnqueueError = "peer offline";
        var job = await _h.AddJob();
        _h.Slskd.DeleteResult = SlskdDeleteResult.NotFound;

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(job.Id), true, CancellationToken.None)).ShouldBeTrue();

        _h.Status.RemoteDeleteForbidden.ShouldBeFalse();
    }

    [Fact]
    public async Task Retry_reenqueues_failed()
    {
        _h.Slskd.EnqueueError = "peer offline";
        var job = await _h.AddJob();
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        _h.Slskd.EnqueueError = null;

        var result = await _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None);

        result.Ok.ShouldBeTrue();
        result.NzoId.ShouldBe(DownloadManager.NzoId(job.Id));
        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Queued);
        after.Files[0].BatchId.ShouldNotBe(job.Files[0].BatchId);
        after.FailMessage.ShouldBeNull();
        after.BytesDone.ShouldBe(0);
        after.PlaceInQueue.ShouldBeNull();
        after.LastProgressAt.ShouldBe(DownloadHarness.Start + TimeSpan.FromMinutes(10));
        _h.Slskd.Enqueues.Count.ShouldBe(2);
        _h.Slskd.Enqueues[1].BatchId.ShouldBe(after.Files[0].BatchId);
        _h.Slskd.Enqueues[1].Destination.ShouldBe("slskarr/" + job.Id.ToString("N"));
    }

    [Fact]
    public async Task Retry_cancels_old_transfer_and_rejects_non_failed()
    {
        var job = await _h.AddJob();
        (await _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None)).Ok.ShouldBeFalse();

        await _h.Poll();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Completed, Rejected");
        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Failed);

        (await _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None)).Ok.ShouldBeTrue();

        var oldTransfer = _h.Slskd.TransferIdFor(job.Files[0].BatchId);
        _h.Slskd.Cancels.ShouldContain((oldTransfer, true));
        _h.Job(job.Id).Files[0].SlskdTransferId.ShouldNotBe(oldTransfer);
        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);
    }

    private DownloadJobEntity OnlyJob()
    {
        using var db = _h.Db.CreateDbContext();
        return db.DownloadJobs.AsNoTracking().Include(j => j.Files).Single();
    }

    private int JobCount()
    {
        using var db = _h.Db.CreateDbContext();
        return db.DownloadJobs.Count();
    }

    [Fact]
    public async Task Add_enqueue_is_not_cancelled_when_the_caller_goes_away()
    {
        using var cts = new CancellationTokenSource();
        var gate = _h.Slskd.EnqueueGate = new TaskCompletionSource();
        var add = _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", cts.Token);
        await SearchHarness.Until(() => _h.Slskd.Enqueues.Count == 1, "the enqueue is in flight");

        await cts.CancelAsync();
        gate.SetResult();
        var result = await add.Within();

        result.Ok.ShouldBeTrue();
        var job = OnlyJob();
        job.State.ShouldBe(DownloadState.Queued);
        job.Files[0].SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(job.Files[0].BatchId));
    }

    [Fact]
    public async Task Add_enqueue_timeout_keeps_job_when_slskd_has_the_transfer()
    {
        _h.Slskd.EnqueueGate = new TaskCompletionSource(); // never answers
        _h.Slskd.BatchBeforeFailure = 1;
        var add = _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);
        await SearchHarness.Until(() => _h.Slskd.Enqueues.Count == 1, "the enqueue is in flight");

        _h.Time.Advance(DownloadManager.EnqueueTimeout);
        var result = await add.Within();

        result.Ok.ShouldBeTrue();
        var job = OnlyJob();
        result.NzoId.ShouldBe(DownloadManager.NzoId(job.Id));
        job.State.ShouldBe(DownloadState.Queued);
        job.Files[0].SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(job.Files[0].BatchId));
    }

    [Fact]
    public async Task Add_unavailable_keeps_job_when_slskd_has_the_transfer()
    {
        _h.Slskd.EnqueueThrows = true;
        _h.Slskd.BatchBeforeFailure = 1;

        var result = await _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);

        result.Ok.ShouldBeTrue();
        var job = OnlyJob();
        job.State.ShouldBe(DownloadState.Queued);
        job.Files[0].SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(job.Files[0].BatchId));
        _h.Slskd.Enqueues.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Add_unavailable_with_empty_batch_stays_queued_until_the_grace_settles_it()
    {
        _h.Slskd.EnqueueThrows = true;
        _h.Slskd.BatchBeforeFailure = 0;

        var result = await _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);

        result.Ok.ShouldBeTrue();
        var job = OnlyJob();
        job.State.ShouldBe(DownloadState.Queued);
        job.Files[0].SlskdTransferId.ShouldBeNull();

        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);

        _h.Time.Advance(DownloadMonitor.AddGrace);
        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Failed);
    }

    [Fact]
    public async Task Add_unavailable_without_batch_removes_job_and_throws()
    {
        _h.Slskd.EnqueueThrows = true;

        await Should.ThrowAsync<SlskdUnavailableException>(
            () => _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None));

        JobCount().ShouldBe(0);
    }

    [Fact]
    public async Task Add_unavailable_and_probe_fails_removes_job_and_throws()
    {
        _h.Slskd.EnqueueThrows = true;
        _h.Slskd.BatchBeforeFailure = 1;
        _h.Slskd.Unavailable = true;

        await Should.ThrowAsync<SlskdUnavailableException>(
            () => _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None));

        JobCount().ShouldBe(0);
    }

    [Fact]
    public async Task Add_enqueue_timeout_without_batch_removes_job_and_reports_unavailable()
    {
        _h.Slskd.EnqueueGate = new TaskCompletionSource();
        var add = _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);
        await SearchHarness.Until(() => _h.Slskd.Enqueues.Count == 1, "the enqueue is in flight");

        _h.Time.Advance(DownloadManager.EnqueueTimeout);

        await Should.ThrowAsync<SlskdUnavailableException>(() => add.Within());
        JobCount().ShouldBe(0);
    }

    private async Task<DownloadJobEntity> FailedJob()
    {
        _h.Slskd.EnqueueError = "peer offline";
        var job = await _h.AddJob();
        _h.Slskd.EnqueueError = null;
        return job;
    }

    [Fact]
    public async Task Retry_unavailable_keeps_job_when_slskd_has_the_transfer()
    {
        var job = await FailedJob();
        _h.Slskd.EnqueueThrows = true;
        _h.Slskd.BatchBeforeFailure = 1;

        var result = await _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None);

        result.Ok.ShouldBeTrue();
        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Queued);
        after.Files[0].BatchId.ShouldNotBe(job.Files[0].BatchId);
        after.Files[0].SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(after.Files[0].BatchId));
    }

    [Fact]
    public async Task Retry_unavailable_with_empty_batch_stays_queued()
    {
        var job = await FailedJob();
        _h.Slskd.EnqueueThrows = true;
        _h.Slskd.BatchBeforeFailure = 0;

        (await _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None)).Ok.ShouldBeTrue();

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Queued);
        after.Files[0].SlskdTransferId.ShouldBeNull();
    }

    [Fact]
    public async Task Retry_unavailable_without_batch_stays_failed_and_throws()
    {
        var job = await FailedJob();
        _h.Slskd.EnqueueThrows = true;

        await Should.ThrowAsync<SlskdUnavailableException>(
            () => _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None));

        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Failed);
        after.Files[0].BatchId.ShouldBe(job.Files[0].BatchId);
    }

    [Fact]
    public async Task Retry_enqueue_is_not_cancelled_when_the_caller_goes_away()
    {
        var job = await FailedJob();
        using var cts = new CancellationTokenSource();
        var gate = _h.Slskd.EnqueueGate = new TaskCompletionSource();
        var retry = _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), cts.Token);
        await SearchHarness.Until(() => _h.Slskd.Enqueues.Count == 2, "the retry enqueue is in flight");

        await cts.CancelAsync();
        gate.SetResult();

        (await retry.Within()).Ok.ShouldBeTrue();
        var after = _h.Job(job.Id);
        after.State.ShouldBe(DownloadState.Queued);
        after.Files[0].SlskdTransferId.ShouldBe(_h.Slskd.TransferIdFor(after.Files[0].BatchId));
    }
}
