using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Slskarr.Configuration;

namespace Slskarr.Soulseek;

public sealed class SlskdClient(HttpClient http, TimeProvider time) : ISlskdClient
{
    private const string Api = "api/v0/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan[] BusyBackoff =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    /// <summary>Applies base address (always ending in '/', so path bases survive) and the API key header.</summary>
    public static void Configure(HttpClient client, SlskdOptions options)
    {
        var url = options.Url ?? throw new InvalidOperationException("Slskarr:Slskd:Url is not configured");
        var text = url.AbsoluteUri;
        client.BaseAddress = text.EndsWith('/') ? url : new Uri(text + "/");
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
    }

    public async Task<Guid> StartSearchAsync(string text, int timeoutSeconds, int responseLimit, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var body = new
        {
            id,
            searchText = text,
            searchTimeout = timeoutSeconds * 1000, // slskd takes milliseconds
            responseLimit,
            filterResponses = true,
        };
        using var response = await SendAsync(() => Req(HttpMethod.Post, "searches", body), ct);
        await EnsureSuccessAsync(response, ct);
        return id;
    }

    public async Task<SlskdSearch?> GetSearchAsync(Guid id, bool includeResponses, CancellationToken ct)
    {
        var path = $"searches/{id}?includeResponses={(includeResponses ? "true" : "false")}";
        using var response = await SendAsync(() => Req(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<SlskdSearch>(response, ct);
    }

    public async Task DeleteSearchAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(() => Req(HttpMethod.Delete, $"searches/{id}"), ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, ct);
        }
    }

    public async Task<SlskdEnqueueResult> EnqueueBatchAsync(
        Guid batchId, string username, IReadOnlyList<SlskdEnqueueFile> files, string destination, CancellationToken ct)
    {
        var body = new
        {
            id = batchId,
            username,
            files = files.Select(f => new { filename = f.Filename, size = f.Size }).ToArray(),
            options = new { destination },
        };

        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(
                () => Req(HttpMethod.Post, "transfers/downloads/batches", body), ct, passServerErrors: true);

            switch ((int)response.StatusCode)
            {
                case 201:
                    return new SlskdEnqueueResult(true, (await ReadEnqueueAsync(response, ct)).Batch, null);
                case 200 or 207:
                    var failed = await ReadEnqueueAsync(response, ct);
                    var message = failed.Failures?.FirstOrDefault()?.Message ?? "slskd did not enqueue the file";
                    return new SlskdEnqueueResult(false, failed.Batch, message);
                case 404:
                    return new SlskdEnqueueResult(false, null, "peer offline");
                case 400 or 409:
                    return new SlskdEnqueueResult(false, null, $"slskd rejected the enqueue ({(int)response.StatusCode}): {await BodyAsync(response, ct)}");
                case 429 when attempt < BusyBackoff.Length:
                    await Task.Delay(BusyBackoff[attempt], time, ct);
                    continue;
                case 429:
                    return new SlskdEnqueueResult(false, null, "slskd busy (429)");
                case >= 500 and not (502 or 503 or 504):
                    // slskd's catch-all for ordinary peer failures (e.g. the peer cannot be connected to).
                    return new SlskdEnqueueResult(false, null,
                        $"slskd could not enqueue the file ({(int)response.StatusCode}): {(await BodyAsync(response, ct)).Trim().Trim('"').Trim()}");
                default:
                    await EnsureSuccessAsync(response, ct);
                    throw new SlskdUnavailableException($"slskd returned unexpected status {(int)response.StatusCode}");
            }
        }
    }

    public async Task<SlskdBatch?> GetBatchAsync(Guid batchId, CancellationToken ct)
    {
        using var response = await SendAsync(() => Req(HttpMethod.Get, $"transfers/downloads/batches/{batchId}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<SlskdBatch>(response, ct);
    }

    public async Task<int?> GetPlaceInQueueAsync(string username, Guid transferId, CancellationToken ct)
    {
        var path = $"transfers/downloads/{Uri.EscapeDataString(username)}/{transferId}/position";
        using var response = await SendAsync(() => Req(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        return await ReadAsync<int?>(response, ct);
    }

    public async Task CancelDownloadAsync(string username, Guid transferId, bool remove, CancellationToken ct)
    {
        var path = $"transfers/downloads/{Uri.EscapeDataString(username)}/{transferId}?remove={(remove ? "true" : "false")}";
        using var response = await SendAsync(() => Req(HttpMethod.Delete, path), ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, ct);
        }
    }

    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => Req(HttpMethod.Get, "application/version"), ct);
        await EnsureSuccessAsync(response, ct);
        return (await ReadAsync<string>(response, ct)).Trim('"');
    }

    public async Task<IReadOnlyList<SlskdLocalFile>?> ListDownloadDirAsync(string relativeDir, CancellationToken ct)
    {
        var path = $"files/downloads/directories/{EncodePath(relativeDir)}?recursive=true";
        using var response = await SendAsync(() => Req(HttpMethod.Get, path), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        var root = await ReadAsync<DirDto>(response, ct);
        var files = new List<SlskdLocalFile>();
        Flatten(root, files);
        return files;
    }

    public Task<SlskdDeleteResult> DeleteDownloadDirAsync(string relativeDir, CancellationToken ct) =>
        DeleteAsync($"files/downloads/directories/{EncodePath(relativeDir)}", ct);

    public Task<SlskdDeleteResult> DeleteIncompleteFileAsync(string relativePath, CancellationToken ct) =>
        DeleteAsync($"files/incomplete/files/{EncodePath(relativePath)}", ct);

    private async Task<SlskdDeleteResult> DeleteAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => Req(HttpMethod.Delete, path), ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return SlskdDeleteResult.NotFound;
            case HttpStatusCode.Forbidden:
                return SlskdDeleteResult.Forbidden;
            default:
                await EnsureSuccessAsync(response, ct);
                return SlskdDeleteResult.Deleted;
        }
    }

    /// <summary>The files API takes a relative directory or file as URL-escaped base64 in one route segment.</summary>
    private static string EncodePath(string relativePath) =>
        Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath)));

    private static void Flatten(DirDto dir, List<SlskdLocalFile> into)
    {
        foreach (var file in dir.Files ?? [])
        {
            into.Add(new SlskdLocalFile(file.Name, file.Length));
        }

        foreach (var sub in dir.Directories ?? [])
        {
            Flatten(sub, into);
        }
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    /// <summary>
    /// Sends a request; network failures, timeouts and 5xx become <see cref="SlskdUnavailableException"/>.
    /// With <paramref name="passServerErrors"/>, 5xx responses are returned for the caller to map.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> make, CancellationToken ct, bool passServerErrors = false)
    {
        using var request = make();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SlskdUnavailableException($"slskd unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SlskdUnavailableException("slskd request timed out", ex);
        }

        if ((int)response.StatusCode >= 500 && !passServerErrors)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new SlskdUnavailableException($"slskd returned server error {status}");
        }

        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new SlskdUnavailableException(
                $"slskd returned unexpected status {(int)response.StatusCode}: {await BodyAsync(response, ct)}");
        }
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length > 200 ? text[..200] : text;
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new SlskdUnavailableException("slskd returned an empty response");
        }
        catch (JsonException ex)
        {
            throw new SlskdUnavailableException("slskd returned an unreadable response", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SlskdUnavailableException($"slskd unreachable: {ex.Message}", ex);
        }
    }

    private static async Task<EnqueueDto> ReadEnqueueAsync(HttpResponseMessage response, CancellationToken ct) =>
        await ReadAsync<EnqueueDto>(response, ct);

    private sealed record EnqueueDto(SlskdBatch? Batch, List<FailureDto>? Failures);

    private sealed record FailureDto(string? Filename, string? Message);

    private sealed record DirDto(List<FileDto>? Files, List<DirDto>? Directories);

    private sealed record FileDto(string Name, long Length);
}
