using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Slskarr.Downloads;

namespace Slskarr.IntegrationTests;

/// <summary>
/// In-process slskd (<c>/api/v0</c>) on a <see cref="TestServer"/>, with scriptable peers, searches and
/// transfers. Its behaviour follows the slskd source: searches start in state <c>Requested</c> and
/// complete later; batch enqueue answers 201, 409 for a known batch id, 404 for an offline peer; the
/// queue position call answers 500 when the peer is unreachable. Every request is recorded.
/// </summary>
/// <remarks>
/// Files follow slskd's on-disk layout. A transfer in progress writes
/// <c>incomplete/&lt;username&gt;/&lt;remote folder, root stripped&gt;/&lt;file&gt;</c>; a finished one is moved to
/// <c>downloads/&lt;batch destination&gt;/&lt;file&gt;</c> (slskd <c>DownloadService</c> + <c>FileService.MoveFile</c>).
/// Time is the given <see cref="TimeProvider"/>, shared with the Slskarr host.
/// </remarks>
public sealed class FakeSlskd : IDisposable
{
    public sealed record FakeFile(
        string Filename, long Size, int? BitRate = null, int? BitDepth = null, int? SampleRate = null, int? Length = null, bool? IsVbr = null);

    public sealed record RecordedRequest(string Method, string Path, string Query, string Body);

    /// <summary>From <see cref="At"/> after the enqueue (fake time), the transfer reports this state.</summary>
    public sealed record TransferStep(TimeSpan At, string State, long Bytes = 0, int? PlaceInQueue = null);

    private const string Api = "/api/v0";

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly WebApplication _app;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Search> _searches = [];
    private readonly Dictionary<Guid, Batch> _batches = [];
    private readonly SortedDictionary<string, long> _downloads = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _incomplete = new(StringComparer.Ordinal);
    private Func<Guid, IReadOnlyList<TransferStep>> _script = _ => [new TransferStep(TimeSpan.Zero, "Queued, Remotely")];

    public FakeSlskd(TimeProvider time)
    {
        _time = time;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(RecordAsync);
        Map(_app.MapGroup(Api));
        _app.StartAsync().GetAwaiter().GetResult();
    }

    /// <summary>How long a search runs before it completes and shows its responses (fake time).</summary>
    public TimeSpan SearchDelay { get; set; }

    /// <summary>slskd's <c>remote_file_management</c>; when false the files API refuses deletes with 403.</summary>
    public bool RemoteFileManagement { get; set; } = true;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Files under slskd's downloads directory, as relative paths with '/' separators.</summary>
    public IReadOnlyList<string> DownloadedFiles
    {
        get
        {
            lock (_lock)
            {
                RefreshTransfers();
                return [.. _downloads.Keys];
            }
        }
    }

    /// <summary>Partial files under slskd's incomplete directory, as relative paths with '/' separators.</summary>
    public IReadOnlyList<string> IncompleteFiles
    {
        get
        {
            lock (_lock)
            {
                RefreshTransfers();
                return [.. _incomplete.Keys];
            }
        }
    }

    public HttpMessageHandler CreateHandler() => _app.GetTestServer().CreateHandler();

    /// <summary>A peer sharing <paramref name="files"/>; it answers searches whose words all appear in a file's path.</summary>
    public void AddPeer(string username, IEnumerable<(string Filename, long Size)> files, bool freeSlot, int queueLength, int speed) =>
        AddPeer(username, files.Select(f => new FakeFile(f.Filename, f.Size)), freeSlot, queueLength, speed);

    /// <summary>A peer sharing <paramref name="files"/> with audio attributes.</summary>
    public void AddPeer(string username, IEnumerable<FakeFile> files, bool freeSlot, int queueLength, int speed)
    {
        lock (_lock)
        {
            _peers[username] = new Peer([.. files], freeSlot, queueLength, speed) { Online = true };
        }
    }

    /// <summary>The destination folder (relative to slskd's downloads) the given batch was enqueued with.</summary>
    public string DestinationOf(Guid batchId)
    {
        lock (_lock)
        {
            return _batches[batchId].Destination;
        }
    }

    public void SetOnline(string username, bool online)
    {
        lock (_lock)
        {
            _peers[username].Online = online;
        }
    }

    /// <summary>The steps each newly enqueued batch's transfer goes through, chosen by batch id.</summary>
    public void ScriptTransfer(Func<Guid, IReadOnlyList<TransferStep>> script)
    {
        lock (_lock)
        {
            _script = script;
        }
    }

    public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task RecordAsync(HttpContext ctx, RequestDelegate next)
    {
        ctx.Request.EnableBuffering();
        var body = await new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true).ReadToEndAsync();
        ctx.Request.Body.Position = 0;
        lock (_lock)
        {
            _requests.Add(new RecordedRequest(ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Request.QueryString.Value ?? "", body));
        }

        await next(ctx);
    }

    private void Map(RouteGroupBuilder api)
    {
        api.MapGet("/application/version", () => Results.Json("0.26.0"));

        api.MapPost("/searches", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            lock (_lock)
            {
                var search = new Search(body.GetProperty("id").GetGuid(), body.GetProperty("searchText").GetString()!, _time.GetUtcNow());
                _searches[search.Id] = search;
                return Results.Json(SearchJson(search, includeResponses: false));
            }
        });

        api.MapGet("/searches/{id:guid}", (Guid id, bool? includeResponses) =>
        {
            lock (_lock)
            {
                return _searches.TryGetValue(id, out var search)
                    ? Results.Json(SearchJson(search, includeResponses == true))
                    : Results.NotFound();
            }
        });

        api.MapDelete("/searches/{id:guid}", (Guid id) =>
        {
            lock (_lock)
            {
                return _searches.Remove(id) ? Results.NoContent() : Results.NotFound();
            }
        });

        api.MapPost("/transfers/downloads/batches", async (HttpContext ctx) =>
        {
            var body = await ReadJsonAsync(ctx);
            lock (_lock)
            {
                return Enqueue(body);
            }
        });

        api.MapGet("/transfers/downloads/batches/{id:guid}", (Guid id) =>
        {
            lock (_lock)
            {
                RefreshTransfers();
                return _batches.TryGetValue(id, out var batch) ? Results.Json(BatchJson(batch)) : Results.NotFound();
            }
        });

        api.MapGet("/transfers/downloads/{username}/{id:guid}/position", (string username, Guid id) =>
        {
            lock (_lock)
            {
                RefreshTransfers();
                if (FindTransfer(username, id) is not { } transfer)
                {
                    return Results.NotFound();
                }

                return _peers.TryGetValue(username, out var peer) && peer.Online
                    ? Results.Json(transfer.PlaceInQueue)
                    : Results.Problem("Failed to get place in queue: peer unreachable", statusCode: 500);
            }
        });

        api.MapDelete("/transfers/downloads/{username}/{id:guid}", (string username, Guid id, bool? remove) =>
        {
            lock (_lock)
            {
                RefreshTransfers();
                if (FindTransfer(username, id) is not { } transfer)
                {
                    return Results.NotFound();
                }

                if (!transfer.State.Contains("Completed", StringComparison.Ordinal))
                {
                    transfer.State = "Completed, Cancelled";
                    transfer.Frozen = true;
                }

                transfer.Removed |= remove == true;
                return Results.NoContent();
            }
        });

        api.MapGet("/files/downloads/directories/{dir}", (string dir, bool? recursive) =>
        {
            lock (_lock)
            {
                RefreshTransfers();
                return List(_downloads, Decode(dir), recursive == true);
            }
        });

        api.MapDelete("/files/downloads/directories/{dir}", (string dir) => DeleteDir(_downloads, dir));
        api.MapDelete("/files/incomplete/files/{file}", (string file) => DeleteFile(_incomplete, file));
    }

    private IResult Enqueue(JsonElement body)
    {
        var id = body.GetProperty("id").GetGuid();
        var username = body.GetProperty("username").GetString()!;
        var files = body.GetProperty("files").EnumerateArray().ToList();
        if (files.Count == 0)
        {
            return Results.BadRequest("At least one file is required");
        }

        if (_batches.ContainsKey(id))
        {
            return Results.Conflict($"A batch with ID {id} already exists");
        }

        if (!_peers.TryGetValue(username, out var peer) || !peer.Online)
        {
            return Results.Problem($"User {username} appears to be offline", statusCode: 404);
        }

        var destination = body.TryGetProperty("options", out var options)
            && options.TryGetProperty("destination", out var d) && d.GetString() is { Length: > 0 } dest
                ? dest
                : "";
        var steps = _script(id);
        var batch = new Batch(id, username, destination,
        [
            .. files.Select(f => new Transfer(
                Guid.NewGuid(), id, username, f.GetProperty("filename").GetString()!, f.GetProperty("size").GetInt64(),
                destination, _time.GetUtcNow(), steps)),
        ]);
        _batches[id] = batch;
        RefreshTransfers();
        return Results.Json(new { batch = BatchJson(batch), failures = Array.Empty<object>() }, statusCode: 201);
    }

    private IResult DeleteDir(SortedDictionary<string, long> root, string dir)
    {
        lock (_lock)
        {
            if (!RemoteFileManagement)
            {
                return Results.StatusCode(403);
            }

            RefreshTransfers();
            var prefix = Decode(dir).Trim('/') + "/";
            var under = root.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var key in under)
            {
                root.Remove(key);
            }

            return under.Count > 0 ? Results.NoContent() : Results.NotFound();
        }
    }

    private IResult DeleteFile(SortedDictionary<string, long> root, string file)
    {
        lock (_lock)
        {
            if (!RemoteFileManagement)
            {
                return Results.StatusCode(403);
            }

            RefreshTransfers();
            return root.Remove(Decode(file).TrimStart('/')) ? Results.NoContent() : Results.NotFound();
        }
    }

    private static IResult List(SortedDictionary<string, long> root, string dir, bool recursive)
    {
        var prefix = dir.Trim('/') + "/";
        var files = root
            .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => (FullName: f.Key[prefix.Length..], Length: f.Value))
            .Where(f => recursive || !f.FullName.Contains('/'))
            .Select(f => new { name = f.FullName[(f.FullName.LastIndexOf('/') + 1)..], fullName = f.FullName, length = f.Length })
            .ToList();
        if (files.Count == 0)
        {
            return Results.NotFound();
        }

        return Results.Json(new { name = prefix.TrimEnd('/')[(prefix.TrimEnd('/').LastIndexOf('/') + 1)..], fullName = "", files, directories = Array.Empty<object>() });
    }

    /// <summary>Brings every live transfer to the step its script reaches at the current time, moving files as slskd would.</summary>
    private void RefreshTransfers()
    {
        var now = _time.GetUtcNow();
        foreach (var transfer in _batches.Values.SelectMany(b => b.Transfers))
        {
            if (!transfer.Frozen)
            {
                var elapsed = now - transfer.EnqueuedAt;
                var step = transfer.Steps.LastOrDefault(s => s.At <= elapsed) ?? transfer.Steps[0];
                transfer.State = step.State;
                transfer.BytesTransferred = step.Bytes;
                transfer.PlaceInQueue = step.PlaceInQueue;
            }

            var flags = transfer.State.Split(',', StringSplitOptions.TrimEntries);
            if (flags.Contains("InProgress"))
            {
                _incomplete[IncompletePath(transfer)] = transfer.BytesTransferred;
            }
            else if (flags.Contains("Succeeded") && !transfer.Moved)
            {
                _incomplete.Remove(IncompletePath(transfer));
                var final = string.IsNullOrEmpty(transfer.Destination) ? FileName(transfer.Filename) : $"{transfer.Destination}/{FileName(transfer.Filename)}";
                _downloads[final] = transfer.Size;
                transfer.Moved = true;
            }
        }
    }

    private Transfer? FindTransfer(string username, Guid id) =>
        _batches.Values.SelectMany(b => b.Transfers).FirstOrDefault(t => t.Id == id && t.Username == username && !t.Removed);

    private object SearchJson(Search search, bool includeResponses)
    {
        var complete = _time.GetUtcNow() - search.StartedAt >= SearchDelay;
        var responses = complete ? Responses(search.Text) : [];
        return new
        {
            id = search.Id,
            searchText = search.Text,
            startedAt = search.StartedAt.UtcDateTime,
            state = complete ? "Completed, TimedOut" : _time.GetUtcNow() == search.StartedAt ? "Requested" : "InProgress",
            isComplete = complete,
            fileCount = responses.Sum(r => r.Files.Count),
            responseCount = responses.Count,
            responses = includeResponses ? responses.Select(ResponseJson).ToArray() : [],
        };
    }

    private List<(string Username, Peer Peer, List<FakeFile> Files)> Responses(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return
        [
            .. _peers
                .Where(p => p.Value.Online)
                .Select(p => (p.Key, p.Value, p.Value.Files
                    .Where(f => words.All(w => f.Filename.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .ToList()))
                .Where(r => r.Item3.Count > 0),
        ];
    }

    private static object ResponseJson((string Username, Peer Peer, List<FakeFile> Files) r) => new
    {
        username = r.Username,
        hasFreeUploadSlot = r.Peer.FreeSlot,
        queueLength = r.Peer.QueueLength,
        uploadSpeed = r.Peer.Speed,
        fileCount = r.Files.Count,
        files = r.Files.Select(f => new
        {
            filename = f.Filename,
            size = f.Size,
            extension = "",
            bitRate = f.BitRate,
            bitDepth = f.BitDepth,
            sampleRate = f.SampleRate,
            length = f.Length,
            isVariableBitRate = f.IsVbr,
        }).ToArray(),
        lockedFileCount = 0,
        lockedFiles = Array.Empty<object>(),
    };

    private static object BatchJson(Batch batch) => new
    {
        id = batch.Id,
        username = batch.Username,
        direction = "Download",
        options = new { destination = batch.Destination },
        transfers = batch.Transfers.Where(t => !t.Removed).Select(t => new
        {
            id = t.Id,
            batchId = t.BatchId,
            username = t.Username,
            direction = "Download",
            filename = t.Filename,
            size = t.Size,
            state = t.State,
            bytesTransferred = t.BytesTransferred,
            averageSpeed = 0d,
            placeInQueue = t.PlaceInQueue,
            exception = (string?)null,
        }).ToArray(),
    };

    private static async Task<JsonElement> ReadJsonAsync(HttpContext ctx) =>
        (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement.Clone();

    /// <summary>The files API takes the directory as (URL-escaped) base64 in one route segment.</summary>
    private static string Decode(string dir) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(dir))).Replace('\\', '/');

    /// <summary>slskd's <c>FileSafety.GetFileNameSafely</c> on Linux: the last segment after either slash.</summary>
    private static string FileName(string remotePath) => remotePath.Split('\\', '/')[^1];

    /// <summary>
    /// slskd: <c>incomplete/&lt;username&gt;/&lt;remote directory without its root&gt;/&lt;file&gt;</c>. The layout rules
    /// (root stripping, segment sanitising) are pinned against slskd's <c>FileSafety</c> by <c>StoragePathsTests</c>.
    /// </summary>
    private static string IncompletePath(Transfer t) =>
        StoragePaths.IncompleteFile(t.Username, t.Filename)
            ?? throw new InvalidOperationException($"slskd would not download '{t.Filename}': it names no file");

    private sealed record Search(Guid Id, string Text, DateTimeOffset StartedAt);

    private sealed record Batch(Guid Id, string Username, string Destination, IReadOnlyList<Transfer> Transfers);

    private sealed record Peer(List<FakeFile> Files, bool FreeSlot, int QueueLength, int Speed)
    {
        public bool Online { get; set; }
    }

    private sealed class Transfer(
        Guid id, Guid batchId, string username, string filename, long size, string destination,
        DateTimeOffset enqueuedAt, IReadOnlyList<TransferStep> steps)
    {
        public Guid Id { get; } = id;

        public Guid BatchId { get; } = batchId;

        public string Username { get; } = username;

        public string Filename { get; } = filename;

        public long Size { get; } = size;

        public string Destination { get; } = destination;

        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;

        public IReadOnlyList<TransferStep> Steps { get; } = steps;

        public string State { get; set; } = "Requested";

        public long BytesTransferred { get; set; }

        public int? PlaceInQueue { get; set; }

        /// <summary>Cancelled: the script no longer applies.</summary>
        public bool Frozen { get; set; }

        public bool Removed { get; set; }

        /// <summary>Already moved to the downloads directory (a later delete must not bring it back).</summary>
        public bool Moved { get; set; }
    }
}
