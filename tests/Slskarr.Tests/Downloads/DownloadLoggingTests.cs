using Microsoft.Extensions.Logging;
using Shouldly;

namespace Slskarr.Tests.Downloads;

/// <summary>The download path logs each add, its outcome in slskd, and every job state change.</summary>
public sealed class DownloadLoggingTests : IDisposable
{
    private readonly DownloadHarness _h = new();

    public void Dispose() => _h.Dispose();

    private IEnumerable<string> Lines(LogLevel min = LogLevel.Information) => _h.Logs.Messages(min);

    [Fact]
    public async Task Add_logs_the_release_and_the_enqueue()
    {
        var job = await _h.AddJob();

        var line = Lines().Single(l => l.Contains("Queued download"));
        line.ShouldContain("Frank Herbert - Dune (1965)");
        line.ShouldContain("peer");
        line.ShouldContain("1 file(s)");
        line.ShouldContain("ebooks");
        line.ShouldContain(job.Id.ToString("N"));
    }

    [Fact]
    public async Task Add_of_unknown_release_warns()
    {
        var result = await _h.Manager.AddAsync("nope", "ebooks", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        Lines(LogLevel.Warning).ShouldContain(l => l.Contains("nope") && l.Contains("expired or unknown"));
    }

    [Fact]
    public async Task Add_rejected_by_slskd_warns_with_reason()
    {
        _h.Slskd.EnqueueError = "peer offline";

        await _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None);

        Lines(LogLevel.Warning).ShouldContain(l => l.Contains("Frank Herbert - Dune (1965)") && l.Contains("peer offline"));
    }

    [Fact]
    public async Task Add_with_slskd_down_warns()
    {
        _h.Slskd.EnqueueThrows = true;

        await Should.ThrowAsync<Slskarr.Soulseek.SlskdUnavailableException>(
            () => _h.Manager.AddAsync(DownloadHarness.HitId, "ebooks", CancellationToken.None));

        Lines(LogLevel.Warning).ShouldContain(l => l.Contains("Frank Herbert - Dune (1965)") && l.Contains("slskd"));
    }

    [Fact]
    public async Task Monitor_logs_start_completion_and_storage_path()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "InProgress", 10);
        await _h.Poll();
        await _h.Poll(); // no change: no second line

        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Completed, Succeeded", DownloadHarness.FileSize);
        await _h.Poll();

        Lines().Count(l => l.Contains("Downloading") && l.Contains("Frank Herbert - Dune (1965)")).ShouldBe(1);
        var done = Lines().Single(l => l.Contains("Completed") && l.Contains("Frank Herbert - Dune (1965)"));
        done.ShouldContain($"/downloads/slskarr/{job.Id:N}/Frank Herbert - Dune (1965).epub");
    }

    [Fact]
    public async Task Monitor_warns_on_failure_with_message()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Completed, Rejected");

        await _h.Poll();

        Lines(LogLevel.Warning).ShouldContain(l => l.Contains("Frank Herbert - Dune (1965)") && l.Contains("Soulseek: Rejected"));
    }

    [Fact]
    public async Task Delete_and_retry_are_logged()
    {
        var job = await _h.AddJob();
        _h.Slskd.SetTransfer(job.Files[0].BatchId, "Completed, Rejected");
        await _h.Poll();

        await _h.Manager.RetryAsync("SLSKARR_" + job.Id.ToString("N"), CancellationToken.None);
        await _h.Manager.DeleteAsync("SLSKARR_" + job.Id.ToString("N"), deleteFiles: false, CancellationToken.None);

        Lines().ShouldContain(l => l.Contains("Retrying") && l.Contains("Frank Herbert - Dune (1965)"));
        Lines().ShouldContain(l => l.Contains("Removed") && l.Contains("Frank Herbert - Dune (1965)"));
    }
}
