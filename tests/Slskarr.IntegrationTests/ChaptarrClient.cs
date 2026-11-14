using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Time.Testing;

namespace Slskarr.IntegrationTests;

/// <summary>
/// Talks to Slskarr the way Chaptarr (a Readarr fork) does: <c>NewznabRequestGenerator</c>'s book-search
/// tiers and <c>SabnzbdProxy</c>'s calls, with the same parameters in the same order.
/// </summary>
/// <remarks>
/// While a request is in flight, fake time moves on in small steps, as real time would while Chaptarr
/// waits; that is what lets Slskarr's search sync-wait time out.
/// </remarks>
public sealed class ChaptarrClient(HttpClient http, string apiKey, FakeTimeProvider time)
{
    public const string Category = "ebooks";

    private const string Categories = "7000,7020";
    private const int PageSize = 100;
    private const int HistoryLimit = 60;

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(500);

    // Real time per fake step: room for the search worker to act before a sync-wait runs out (60s fake = 2.4s real).
    private static readonly TimeSpan StepPause = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    public sealed record NewznabItem(string Title, string Enclosure, long Size);

    public sealed record SabSlot(string NzoId, string Status, string Storage, string FailMessage);

    /// <summary>How many Newznab tiers the last <see cref="SearchBookAsync"/> sent.</summary>
    public int SearchTiersSent { get; private set; }

    /// <summary>Tries each tier in order and stops at the first that returns any items.</summary>
    public async Task<IReadOnlyList<NewznabItem>> SearchBookAsync(string author, string title)
    {
        string[] tiers =
        [
            Book($"&author={Esc(author)}&title={Esc(title)}"),
            Book($"&title={Esc(title)}"),
            Search($"&q={Esc(title)}+{Esc(author)}"),
            Search($"&q={Esc(author)}+{Esc(title)}"),
            Search($"&q={Esc(title)}"),
        ];
        SearchTiersSent = 0;
        foreach (var url in tiers)
        {
            SearchTiersSent++;
            var items = await FeedAsync(url);
            if (items.Count > 0)
            {
                return items;
            }
        }

        return [];
    }

    /// <summary>The RSS poll: a book search with no terms.</summary>
    public Task<IReadOnlyList<NewznabItem>> RssAsync() => FeedAsync(Book(""));

    public async Task<byte[]> DownloadNzbAsync(string enclosureUrl)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, enclosureUrl));
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<string> AddFileAsync(byte[] nzb, string filename)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(nzb);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-nzb");
        form.Add(file, "name", filename);
        var json = await SabAsync(HttpMethod.Post, $"mode=addfile&cat={Category}&priority=0", form);
        Require(json);
        return json.GetProperty("nzo_ids")[0].GetString()!;
    }

    public async Task<IReadOnlyList<SabSlot>> QueueAsync()
    {
        var json = await SabAsync(HttpMethod.Get, $"mode=queue&start=0&limit=0&category={Category}");
        return Slots(json.GetProperty("queue"));
    }

    public async Task<IReadOnlyList<SabSlot>> HistoryAsync()
    {
        var json = await SabAsync(HttpMethod.Get, $"mode=history&start=0&limit={HistoryLimit}&category={Category}");
        return Slots(json.GetProperty("history"));
    }

    public async Task RemoveFromHistoryAsync(string nzoId, bool deleteData, bool failed)
    {
        var json = await SabAsync(HttpMethod.Get,
            $"mode=history&name=delete&del_files={(deleteData ? 1 : 0)}&value={nzoId}&archive={(failed ? 0 : 1)}");
        Require(json);
    }

    public async Task<string> RetryAsync(string nzoId)
    {
        var json = await SabAsync(HttpMethod.Get, $"mode=retry&value={nzoId}");
        Require(json);
        return json.GetProperty("nzo_id").GetString()!;
    }

    private string Book(string parameters) => Newznab("book", parameters);

    private string Search(string parameters) => Newznab("search", parameters);

    private string Newznab(string type, string parameters) =>
        $"/newznab/api?t={type}&cat={Categories}&extended=1&apikey={apiKey}&offset=0&limit={PageSize}{parameters}";

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private async Task<IReadOnlyList<NewznabItem>> FeedAsync(string url)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());
        if (doc.Root?.Name == "error")
        {
            throw new InvalidOperationException($"Newznab error: {doc.Root}");
        }

        return
        [
            .. doc.Descendants("item").Select(i => new NewznabItem(
                (string)i.Element("title")!,
                (string)i.Element("enclosure")!.Attribute("url")!,
                (long)i.Element("size")!)),
        ];
    }

    private async Task<JsonElement> SabAsync(HttpMethod method, string parameters, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, $"/sabnzbd/api?{parameters}&apikey={apiKey}&output=json") { Content = content };
        using var response = await SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static void Require(JsonElement json)
    {
        if (!json.GetProperty("status").GetBoolean())
        {
            throw new InvalidOperationException($"SABnzbd error: {json}");
        }
    }

    private static List<SabSlot> Slots(JsonElement list) =>
    [
        .. list.GetProperty("slots").EnumerateArray().Select(s => new SabSlot(
            s.GetProperty("nzo_id").GetString()!,
            s.GetProperty("status").GetString()!,
            s.TryGetProperty("storage", out var storage) ? storage.GetString()! : "",
            s.TryGetProperty("fail_message", out var fail) ? fail.GetString()! : "")),
    ];

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var send = http.SendAsync(request);
            var deadline = DateTime.UtcNow + Guard;
            while (!send.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"{request.Method} {request.RequestUri} did not answer");
                }

                await Task.WhenAny(send, Task.Delay(StepPause));
                if (!send.IsCompleted)
                {
                    time.Advance(Step);
                }
            }

            var response = await send;
            response.EnsureSuccessStatusCode();
            return response;
        }
    }
}
