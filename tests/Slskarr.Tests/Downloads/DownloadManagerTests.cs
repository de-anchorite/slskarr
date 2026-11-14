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
        job.BatchId.ShouldBe(job.Id);
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
        _h.Slskd.SetTransfer(completed.BatchId, "Completed, Succeeded", DownloadHarness.FileSize);
        await _h.Poll();
        _h.Job(completed.Id).State.ShouldBe(DownloadState.Completed);

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(completed.Id), true, CancellationToken.None)).ShouldBeTrue();

        _h.Slskd.DeletedDownloadDirs.ShouldBe(["slskarr/" + completed.Id.ToString("N")]);
        _h.Slskd.DeletedIncompleteDirs.ShouldBeEmpty();
        _h.Slskd.Cancels.ShouldContain((_h.Slskd.TransferIdFor(completed.BatchId), true));
        _h.Job(completed.Id).State.ShouldBe(DownloadState.Deleted);
    }

    [Fact]
    public async Task Delete_failed_with_files_calls_incomplete_delete()
    {
        _h.Slskd.EnqueueError = "peer offline";
        var job = await _h.AddJob();

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(job.Id), true, CancellationToken.None)).ShouldBeTrue();

        _h.Slskd.DeletedIncompleteDirs.ShouldBe(["slskarr/" + job.Id.ToString("N")]);
        _h.Slskd.DeletedDownloadDirs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_without_files_keeps_directories_and_cancels_active_transfer()
    {
        var job = await _h.AddJob();
        await _h.Poll();

        (await _h.Manager.DeleteAsync(DownloadManager.NzoId(job.Id), false, CancellationToken.None)).ShouldBeTrue();

        _h.Slskd.DeletedDownloadDirs.ShouldBeEmpty();
        _h.Slskd.DeletedIncompleteDirs.ShouldBeEmpty();
        _h.Slskd.Cancels.ShouldBe([(_h.Slskd.TransferIdFor(job.BatchId), true)]);
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
        after.BatchId.ShouldNotBe(job.BatchId);
        after.FailMessage.ShouldBeNull();
        after.BytesDone.ShouldBe(0);
        after.PlaceInQueue.ShouldBeNull();
        after.LastProgressAt.ShouldBe(DownloadHarness.Start + TimeSpan.FromMinutes(10));
        _h.Slskd.Enqueues.Count.ShouldBe(2);
        _h.Slskd.Enqueues[1].BatchId.ShouldBe(after.BatchId);
        _h.Slskd.Enqueues[1].Destination.ShouldBe("slskarr/" + job.Id.ToString("N"));
    }

    [Fact]
    public async Task Retry_cancels_old_transfer_and_rejects_non_failed()
    {
        var job = await _h.AddJob();
        (await _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None)).Ok.ShouldBeFalse();

        await _h.Poll();
        _h.Slskd.SetTransfer(job.BatchId, "Completed, Rejected");
        await _h.Poll();
        _h.Job(job.Id).State.ShouldBe(DownloadState.Failed);

        (await _h.Manager.RetryAsync(DownloadManager.NzoId(job.Id), CancellationToken.None)).Ok.ShouldBeTrue();

        var oldTransfer = _h.Slskd.TransferIdFor(job.BatchId);
        _h.Slskd.Cancels.ShouldContain((oldTransfer, true));
        _h.Job(job.Id).SlskdTransferId.ShouldNotBe(oldTransfer);
        _h.Job(job.Id).State.ShouldBe(DownloadState.Queued);
    }
}
