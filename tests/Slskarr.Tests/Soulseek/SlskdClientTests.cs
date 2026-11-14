using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Slskarr.Configuration;
using Slskarr.Soulseek;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Slskarr.Tests.Soulseek;

public sealed class SlskdClientTests : IDisposable
{
    private const string Key = "secret-key";
    private const string Dest = "slskarr/0123456789abcdef0123456789abcdef";
    private const string Remote = @"@@abc\Books\A - B.epub";

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly RecordingTime _time = new();

    public void Dispose() => _server.Stop();

    private SlskdClient Client(string? pathBase = null)
    {
        var http = new HttpClient();
        SlskdClient.Configure(http, new SlskdOptions { Url = new Uri(_server.Url + pathBase), ApiKey = Key });
        return new SlskdClient(http, _time);
    }

    private void Stub(string method, string path, int status, string? body = null)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null)
        {
            response = response.WithHeader("Content-Type", "application/json").WithBody(body);
        }

        _server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(response);
    }

    private WireMock.Logging.ILogEntry Only() => _server.LogEntries.ShouldHaveSingleItem();

    private static void ShouldCarryKey(WireMock.Logging.ILogEntry entry) =>
        entry.RequestMessage!.Headers!["X-API-Key"].ShouldBe([Key]);

    private static string B64(string s) => Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(s)));

    [Theory]
    [InlineData(204, SlskdDeleteResult.Deleted)]
    [InlineData(404, SlskdDeleteResult.NotFound)]
    [InlineData(403, SlskdDeleteResult.Forbidden)]
    public async Task DeleteDownloadDir_uses_escaped_base64_route(int status, SlskdDeleteResult expected)
    {
        var handler = new RecordingHandler((HttpStatusCode)status);
        var client = new SlskdClient(new HttpClient(handler) { BaseAddress = new Uri("http://slskd.test/") }, _time);

        var result = await client.DeleteDownloadDirAsync(Dest, default);

        result.ShouldBe(expected);
        handler.Requests.ShouldHaveSingleItem().RequestUri!.AbsoluteUri
            .ShouldBe($"http://slskd.test/api/v0/files/downloads/directories/{B64(Dest)}");
        B64(Dest).ShouldContain("%3D"); // base64 padding is escaped, proving the route is escaped
        handler.Requests[0].Method.ShouldBe(HttpMethod.Delete);
    }

    [Fact]
    public async Task DeleteIncompleteDir_uses_incomplete_route()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var client = new SlskdClient(new HttpClient(handler) { BaseAddress = new Uri("http://slskd.test/") }, _time);

        (await client.DeleteIncompleteDirAsync(Dest, default)).ShouldBe(SlskdDeleteResult.Deleted);

        handler.Requests.ShouldHaveSingleItem().RequestUri!.AbsoluteUri
            .ShouldBe($"http://slskd.test/api/v0/files/incomplete/directories/{B64(Dest)}");
    }

    [Fact]
    public async Task ListDownloadDir_flattens_files_and_maps_404_to_null()
    {
        Stub("GET", $"/api/v0/files/downloads/directories/{Convert.ToBase64String(Encoding.UTF8.GetBytes(Dest))}", 200,
            """{"name":"x","files":[{"name":"a.epub","length":10}],"directories":[{"name":"s","files":[{"name":"b.pdf","length":20}],"directories":[]}]}""");
        var files = await Client().ListDownloadDirAsync(Dest, default);
        files.ShouldNotBeNull().ShouldBe([new SlskdLocalFile("a.epub", 10), new SlskdLocalFile("b.pdf", 20)]);
        Only().RequestMessage!.Query!["recursive"].ShouldBe(["true"]);

        var handler = new RecordingHandler(HttpStatusCode.NotFound);
        var missing = new SlskdClient(new HttpClient(handler) { BaseAddress = new Uri("http://slskd.test/") }, _time);
        (await missing.ListDownloadDirAsync(Dest, default)).ShouldBeNull();
    }

    [Fact]
    public async Task StartSearch_posts_expected_body()
    {
        Stub("POST", "/api/v0/searches", 200, "{}");

        var id = await Client().StartSearchAsync("dune herbert", 15, 100, default);

        var entry = Only();
        ShouldCarryKey(entry);
        var body = JsonNode.Parse(entry.RequestMessage!.Body!)!;
        body["id"]!.GetValue<Guid>().ShouldBe(id);
        body["searchText"]!.GetValue<string>().ShouldBe("dune herbert");
        body["searchTimeout"]!.GetValue<int>().ShouldBe(15000); // slskd expects milliseconds
        body["responseLimit"]!.GetValue<int>().ShouldBe(100);
        body["filterResponses"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task GetSearch_includes_responses_query()
    {
        var id = Guid.NewGuid();
        Stub("GET", $"/api/v0/searches/{id}", 200,
            """{"id":"%ID%","state":"Completed, TimedOut","isComplete":true,"responseCount":1,"responses":[{"username":"bob","hasFreeUploadSlot":true,"queueLength":2,"uploadSpeed":100,"files":[{"filename":"@@a\\b.epub","size":5,"extension":"epub"}]}]}"""
                .Replace("%ID%", id.ToString()));

        var search = await Client().GetSearchAsync(id, true, default);

        search.ShouldNotBeNull();
        search.IsComplete.ShouldBeTrue();
        search.State.ShouldBe("Completed, TimedOut");
        search.Responses.ShouldNotBeNull().Single().Files.Single().Filename.ShouldBe(@"@@a\b.epub");
        var entry = Only();
        ShouldCarryKey(entry);
        entry.RequestMessage!.Query!["includeResponses"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task GetSearch_maps_404_to_null()
    {
        var id = Guid.NewGuid();
        Stub("GET", $"/api/v0/searches/{id}", 404);
        (await Client().GetSearchAsync(id, false, default)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteSearch_sends_delete()
    {
        var id = Guid.NewGuid();
        Stub("DELETE", $"/api/v0/searches/{id}", 204);
        await Client().DeleteSearchAsync(id, default);
        Only().RequestMessage!.Method.ShouldBe("DELETE");
    }

    [Fact]
    public async Task EnqueueBatch_sends_remote_path_verbatim()
    {
        var batchId = Guid.NewGuid();
        Stub("POST", "/api/v0/transfers/downloads/batches", 201,
            $$"""{"batch":{"id":"{{batchId}}","username":"bob","transfers":[{"id":"{{Guid.NewGuid()}}","batchId":"{{batchId}}","username":"bob","filename":"x","size":9,"state":"Requested","bytesTransferred":0,"averageSpeed":0}]},"failures":[]}""");

        var result = await Client().EnqueueBatchAsync(batchId, "bob", Remote, 9, Dest, default);

        result.Ok.ShouldBeTrue();
        result.Batch!.Transfers.Single().State.ShouldBe("Requested");
        var entry = Only();
        ShouldCarryKey(entry);
        var body = JsonNode.Parse(entry.RequestMessage!.Body!)!;
        body["id"]!.GetValue<Guid>().ShouldBe(batchId);
        body["username"]!.GetValue<string>().ShouldBe("bob");
        body["files"]![0]!["filename"]!.GetValue<string>().ShouldBe(Remote);
        body["files"]![0]!["size"]!.GetValue<long>().ShouldBe(9);
        body["options"]!["destination"]!.GetValue<string>().ShouldBe(Dest);
    }

    [Theory]
    [InlineData(207)]
    [InlineData(200)]
    public async Task EnqueueBatch_maps_207_failures(int status)
    {
        Stub("POST", "/api/v0/transfers/downloads/batches", status,
            """{"batch":null,"failures":[{"filename":"x","message":"already in progress"}]}""");

        var result = await Client().EnqueueBatchAsync(Guid.NewGuid(), "bob", Remote, 9, Dest, default);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("already in progress");
    }

    [Fact]
    public async Task EnqueueBatch_retries_429()
    {
        var handler = new SequenceHandler(
            HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.Created);
        var client = new SlskdClient(new HttpClient(handler) { BaseAddress = new Uri("http://slskd.test/") }, _time);

        var result = await client.EnqueueBatchAsync(Guid.NewGuid(), "bob", Remote, 9, Dest, default);

        result.Ok.ShouldBeTrue();
        handler.Calls.ShouldBe(3);
        _time.Delays.ShouldBe([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)]);
    }

    [Fact]
    public async Task EnqueueBatch_gives_up_after_three_retries_of_429()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests);
        var client = new SlskdClient(new HttpClient(handler) { BaseAddress = new Uri("http://slskd.test/") }, _time);

        var result = await client.EnqueueBatchAsync(Guid.NewGuid(), "bob", Remote, 9, Dest, default);

        result.Ok.ShouldBeFalse();
        handler.Calls.ShouldBe(4);
        _time.Delays.ShouldBe([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)]);
    }

    [Fact]
    public async Task EnqueueBatch_404_is_peer_offline()
    {
        Stub("POST", "/api/v0/transfers/downloads/batches", 404, "\"User bob appears to be offline\"");
        var result = await Client().EnqueueBatchAsync(Guid.NewGuid(), "bob", Remote, 9, Dest, default);
        result.ShouldBe(new SlskdEnqueueResult(false, null, "peer offline"));
    }

    [Fact]
    public async Task GetBatch_maps_404_to_null()
    {
        var id = Guid.NewGuid();
        Stub("GET", $"/api/v0/transfers/downloads/batches/{id}", 404);
        (await Client().GetBatchAsync(id, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Username_is_url_encoded()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.OK, "3");
        var client = new SlskdClient(new HttpClient(handler) { BaseAddress = new Uri("http://slskd.test/") }, _time);

        (await client.GetPlaceInQueueAsync("a b/c", id, default)).ShouldBe(3);
        await client.CancelDownloadAsync("a b/c", id, true, default);

        handler.Requests[0].RequestUri!.AbsoluteUri
            .ShouldBe($"http://slskd.test/api/v0/transfers/downloads/a%20b%2Fc/{id}/position");
        handler.Requests[1].RequestUri!.AbsoluteUri
            .ShouldBe($"http://slskd.test/api/v0/transfers/downloads/a%20b%2Fc/{id}?remove=true");
        handler.Requests[1].Method.ShouldBe(HttpMethod.Delete);
    }

    [Fact]
    public async Task GetVersion_reads_plain_string()
    {
        Stub("GET", "/api/v0/application/version", 200, "\"0.26.1\"");
        (await Client().GetVersionAsync(default)).ShouldBe("0.26.1");
    }

    [Fact]
    public async Task Base_address_with_path_base_keeps_api_route()
    {
        Stub("GET", "/slskd/api/v0/application/version", 200, "\"0.26.1\"");
        (await Client("/slskd").GetVersionAsync(default)).ShouldBe("0.26.1");
    }

    [Fact]
    public async Task Server_error_throws_SlskdUnavailable()
    {
        Stub("GET", "/api/v0/application/version", 503, "boom");
        var ex = await Should.ThrowAsync<SlskdUnavailableException>(() => Client().GetVersionAsync(default));
        ex.Message.ShouldNotContain(Key);
    }

    [Fact]
    public async Task Unreachable_throws_SlskdUnavailable_without_key()
    {
        var url = _server.Url;
        _server.Stop();
        var http = new HttpClient();
        SlskdClient.Configure(http, new SlskdOptions { Url = new Uri(url!), ApiKey = Key });
        var ex = await Should.ThrowAsync<SlskdUnavailableException>(
            () => new SlskdClient(http, _time).GetVersionAsync(default));
        ex.ToString().ShouldNotContain(Key);
    }

    [Fact]
    public async Task Http_timeout_throws_SlskdUnavailable_but_caller_cancellation_propagates()
    {
        var client = new SlskdClient(new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://slskd.test/"), Timeout = TimeSpan.FromMilliseconds(50) }, _time);
        await Should.ThrowAsync<SlskdUnavailableException>(() => client.GetVersionAsync(default));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var caller = new SlskdClient(new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://slskd.test/") }, _time);
        await Should.ThrowAsync<OperationCanceledException>(() => caller.GetVersionAsync(cts.Token));
    }

    [Fact]
    public async Task Typed_client_is_wired_through_factory_with_api_key()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/v0/application/version").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("\"0.26.0\""));
        using var factory = new SlskarrFactory()
            .WithConfig(c => c["Slskarr:Slskd:Url"] = server.Url!)
            .WithSlskdHandler(() => new HttpClientHandler());

        var client = factory.Services.GetRequiredService<ISlskdClient>();
        (await client.GetVersionAsync(default)).ShouldBe("0.26.0");

        server.LogEntries.ShouldHaveSingleItem().RequestMessage!.Headers!["X-API-Key"].ShouldBe(["slskd-test-key"]);
    }

    [Theory]
    [InlineData("Requested", true, false, false, false)]
    [InlineData("Queued", true, false, false, false)]
    [InlineData("Queued, Locally", true, false, false, false)]
    [InlineData("Queued, Remotely", true, false, false, false)]
    [InlineData("Initializing", false, true, false, false)]
    [InlineData("InProgress", false, true, false, false)]
    [InlineData("Completed, Succeeded", false, false, true, false)]
    [InlineData("Completed, Cancelled", false, false, false, true)]
    [InlineData("Completed, TimedOut", false, false, false, true)]
    [InlineData("Completed, Errored", false, false, false, true)]
    [InlineData("Completed, Rejected", false, false, false, true)]
    [InlineData("Completed, Aborted", false, false, false, true)]
    [InlineData("Completed", false, false, false, false)]
    [InlineData("None", false, false, false, false)]
    public void SlskdStates_classifies_all_categories(string state, bool queued, bool progress, bool ok, bool failed)
    {
        SlskdStates.IsQueued(state).ShouldBe(queued);
        SlskdStates.IsInProgress(state).ShouldBe(progress);
        SlskdStates.IsSucceeded(state).ShouldBe(ok);
        SlskdStates.IsFailed(state, out _).ShouldBe(failed);
    }

    [Theory]
    [InlineData("Completed, Rejected", "Rejected")]
    [InlineData("Completed, TimedOut", "TimedOut")]
    public void SlskdStates_failure_reason_follows_Completed(string state, string reason)
    {
        SlskdStates.IsFailed(state, out var actual).ShouldBeTrue();
        actual.ShouldBe(reason);
    }

    /// <summary>Completes delays immediately and records the requested durations.</summary>
    private sealed class RecordingTime : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var status = statuses[Math.Min(Calls++, statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"batch":null,"failures":[]}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }
}
