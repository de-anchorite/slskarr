using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Slskarr.Tests;
using System.Text.Json;

namespace Slskarr.IntegrationTests;

/// <summary>
/// The whole music flow over HTTP only: Lidarr-shaped requests in, slskd-shaped requests out to
/// <see cref="FakeSlskd"/>. One batch per disc subfolder; the job folder is deleted through slskd's files API.
/// </summary>
public sealed class MusicFlowTests : IDisposable
{
    private const string ApiKey = "integration-key";
    private const string Peer = "audiophile";
    private const string Root = @"@@abcde\Music\Radiohead\OK Computer (1997)";
    private const long Size = 30_000_000;
    private const string BatchesPath = "/api/v0/transfers/downloads/batches";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollGrace = TimeSpan.FromMilliseconds(200);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "slskarr-integration", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeSlskd _slskd;

    private SlskarrFactory? _factory;
    private HttpClient? _http;

    public MusicFlowTests()
    {
        _slskd = new FakeSlskd(_time);
        _slskd.AddPeer(Peer,
        [
            Flac($@"{Root}\CD1\01 Airbag.flac"),
            Flac($@"{Root}\CD2\01 Lucky.flac"),
            new FakeSlskd.FakeFile($@"{Root}\CD1\cover.jpg", 200_000),
        ], freeSlot: true, queueLength: 0, speed: 1_000_000);
    }

    [Fact]
    public async Task Album_search_grab_download_import_delete()
    {
        _slskd.ScriptTransfer(_ =>
        [
            new(TimeSpan.Zero, "Queued, Remotely"),
            new(TimeSpan.FromSeconds(10), "InProgress", Bytes: Size / 2),
            new(TimeSpan.FromSeconds(20), "Completed, Succeeded", Bytes: Size),
        ]);
        var lidarr = Start();

        var items = await lidarr.SearchAlbumAsync("Radiohead", "OK Computer");
        var top = items[0];
        top.Title.ShouldBe("Radiohead - OK Computer (1997) [FLAC]");
        var nzb = await lidarr.DownloadNzbAsync(top.Enclosure);
        var nzoId = await lidarr.AddFileAsync(nzb, top.Title + ".nzb");
        var jobId = nzoId["SLSKARR_".Length..];

        await PollUntil(async () => (await lidarr.HistoryAsync()).Any(s => s.NzoId == nzoId), "the album reaches history");

        var batches = Posts(BatchesPath);
        batches.Count.ShouldBe(2);
        batches.Select(Destination).Order().ShouldBe([$"slskarr/{jobId}/CD1", $"slskarr/{jobId}/CD2"]);

        var done = (await lidarr.HistoryAsync()).ShouldHaveSingleItem();
        done.Status.ShouldBe("Completed");
        done.Storage.ShouldBe($"/downloads/slskarr/{jobId}");
        _slskd.DownloadedFiles.Count.ShouldBe(2);
        _slskd.DownloadedFiles.ShouldAllBe(f => f.StartsWith($"slskarr/{jobId}/", StringComparison.Ordinal));

        await lidarr.RemoveFromHistoryAsync(nzoId, deleteData: true, failed: false);
        (await lidarr.HistoryAsync()).ShouldBeEmpty();
        _slskd.DownloadedFiles.ShouldBeEmpty();
    }

    public void Dispose()
    {
        _http?.Dispose();
        _factory?.Dispose();
        _slskd.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private static FakeSlskd.FakeFile Flac(string path) =>
        new(path, Size / 2, BitDepth: 16, SampleRate: 44100, Length: 240);

    private LidarrClient Start()
    {
        _factory = new SlskarrFactory(_dataPath, _time)
            .WithConfig(c =>
            {
                c["Slskarr:ApiKey"] = ApiKey;
                c["Slskarr:Slskd:DownloadsPath"] = "/downloads";
            })
            .WithSlskdHandler(_slskd.CreateHandler);
        _http = _factory.CreateClient();
        return new LidarrClient(_http, ApiKey, _time);
    }

    /// <summary>Moves fake time on to the monitor's next poll and checks between polls (see EbookFlowTests).</summary>
    private async Task PollUntil(Func<Task<bool>> done, string because)
    {
        var deadline = DateTime.UtcNow + Guard;
        while (!await done())
        {
            var before = BatchPolls();
            var advanced = TimeSpan.Zero;
            while (BatchPolls() == before)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Timed out at fake {_time.GetUtcNow():O} waiting until {because}");
                }

                if (advanced < PollInterval)
                {
                    _time.Advance(TimeSpan.FromSeconds(1));
                    advanced += TimeSpan.FromSeconds(1);
                    await Task.Delay(2);
                    continue;
                }

                var wait = DateTime.UtcNow + PollGrace;
                while (BatchPolls() == before && DateTime.UtcNow < wait)
                {
                    await Task.Delay(2);
                }

                if (BatchPolls() == before)
                {
                    if (await done())
                    {
                        return;
                    }

                    _time.Advance(TimeSpan.FromSeconds(1));
                }
            }
        }
    }

    private int BatchPolls() =>
        _slskd.Requests.Count(r => r.Method == "GET" && r.Path.StartsWith(BatchesPath + "/", StringComparison.Ordinal));

    private List<FakeSlskd.RecordedRequest> Posts(string path) =>
        [.. _slskd.Requests.Where(r => r.Method == "POST" && r.Path == path)];

    private static string? Destination(FakeSlskd.RecordedRequest batch) =>
        JsonDocument.Parse(batch.Body).RootElement.GetProperty("options").GetProperty("destination").GetString();
}
