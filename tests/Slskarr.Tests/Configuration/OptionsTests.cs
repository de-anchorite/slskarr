using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Shouldly;

namespace Slskarr.Tests.Configuration;

public class OptionsTests
{
    private static SlskarrOptions Resolve(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSlskarrOptions(config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SlskarrOptions>>().Value;
    }

    private static Dictionary<string, string?> Valid(params (string Key, string? Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Slskarr:Slskd:Url"] = "http://slskd:5030",
            ["Slskarr:Slskd:ApiKey"] = "key",
        };
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }
        return values;
    }

    [Fact]
    public void Options_defaults_match_spec()
    {
        var o = Resolve(Valid());

        o.DataPath.ShouldBe("/data");
        o.Slskd.DownloadsPath.ShouldBe("/downloads");
        o.Search.SyncWait.ShouldBe(TimeSpan.FromSeconds(60));
        o.Search.ResultCap.ShouldBe(50);
        o.Search.HourlyCap.ShouldBe(30);
        o.Search.Concurrency.ShouldBe(1);
        o.Search.MinGap.ShouldBe(TimeSpan.FromSeconds(10));
        o.Search.CacheTtl.ShouldBe(TimeSpan.FromHours(6));
        o.Search.QueuedExpiry.ShouldBe(TimeSpan.FromHours(24));
        o.Search.SlskdSearchTimeoutSeconds.ShouldBe(15);
        o.Search.ResponseLimit.ShouldBe(200);
        o.Search.RssWindow.ShouldBe(TimeSpan.FromDays(7));
        o.Search.Retention.ShouldBe(TimeSpan.FromDays(30));
        o.Downloads.StallTimeout.ShouldBe(TimeSpan.FromHours(1));
        o.Downloads.PollInterval.ShouldBe(TimeSpan.FromSeconds(5));
        o.Ebook.SabCategory.ShouldBe("ebooks");
        o.Ebook.MinSizeBytes.ShouldBe(10_240);
        o.Ebook.MaxSizeBytes.ShouldBe(209_715_200);
        o.Ebook.Ranking[0].ShouldBe("NameMatch");
        o.Ebook.Ranking.ShouldBe(["NameMatch", "FreeSlot", "QueueLength", "UploadSpeed", "Format"]);
        o.Ebook.Extensions.ShouldBe(["epub", "azw3", "azw", "mobi", "pdf", "fb2", "djvu", "cbz", "cbr"]);
        o.Ebook.FormatPreference.ShouldBe(["epub", "azw3", "mobi", "azw", "pdf", "fb2", "djvu", "cbz", "cbr"]);
        o.ApiKey.ShouldBeNull();
        o.Music.SabCategory.ShouldBe("music");
        o.Music.MinSizeBytes.ShouldBeNull();
        o.Music.MaxSizeBytes.ShouldBeNull();
        o.Music.Ranking.ShouldBe(["NameMatch", "FreeSlot", "QueueLength", "UploadSpeed", "Format"]);
        o.Music.Extensions.ShouldBe(["flac", "mp3", "m4a", "aac", "ogg", "opus", "wav", "aiff", "aif", "ape", "wv", "alac"]);
        o.Music.FormatPreference.ShouldBe(["flac", "mp3", "m4a", "alac", "ogg", "opus", "aac", "wav", "aiff", "aif", "ape", "wv"]);
    }

    [Fact]
    public void Music_options_configured_arrays_replace_defaults()
    {
        var o = Resolve(Valid(
            ("Slskarr:Music:Extensions:0", ".FLAC"),
            ("Slskarr:Music:FormatPreference:0", "mp3"),
            ("Slskarr:Music:Ranking:0", "Format")));

        o.Music.Extensions.ShouldBe(["flac"]);
        o.Music.FormatPreference.ShouldBe(["mp3"]);
        o.Music.Ranking.ShouldBe(["Format"]);
    }

    [Fact]
    public void Music_options_reject_unknown_ranking_criterion() =>
        Should.Throw<OptionsValidationException>(() => Resolve(Valid(("Slskarr:Music:Ranking:0", "Foo")))).Message.ShouldContain("Slskarr:Music:Ranking");

    [Fact]
    public void Options_configured_arrays_replace_defaults()
    {
        var o = Resolve(Valid(
            ("Slskarr:Ebook:Ranking:0", "FreeSlot"),
            ("Slskarr:Ebook:Ranking:1", "NameMatch"),
            ("Slskarr:Ebook:Extensions:0", "epub"),
            ("Slskarr:Ebook:FormatPreference:0", "pdf")));

        o.Ebook.Ranking.ShouldBe(["FreeSlot", "NameMatch"]);
        o.Ebook.Extensions.ShouldBe(["epub"]);
        o.Ebook.FormatPreference.ShouldBe(["pdf"]);
    }

    [Fact]
    public void Options_normalise_configured_extensions_and_format_preference()
    {
        var o = Resolve(Valid(
            ("Slskarr:Ebook:Extensions:0", " .EPUB "),
            ("Slskarr:Ebook:Extensions:1", "pdf"),
            ("Slskarr:Ebook:FormatPreference:0", ".Mobi"),
            ("Slskarr:Ebook:FormatPreference:1", "epub ")));

        o.Ebook.Extensions.ShouldBe(["epub", "pdf"]);
        o.Ebook.FormatPreference.ShouldBe(["mobi", "epub"]);
    }

    [Theory]
    [InlineData("4", true)]
    [InlineData("0", true)]
    [InlineData("5", false)]
    public void Options_reject_slskd_search_timeout_below_5_seconds(string seconds, bool rejected)
    {
        var values = Valid(("Slskarr:Search:SlskdSearchTimeoutSeconds", seconds));

        if (rejected)
        {
            Should.Throw<OptionsValidationException>(() => Resolve(values)).Message.ShouldContain("SlskdSearchTimeoutSeconds");
        }
        else
        {
            Resolve(values).Search.SlskdSearchTimeoutSeconds.ShouldBe(5);
        }
    }

    [Fact]
    public void Options_reject_missing_slskd_url_or_key()
    {
        var ex = Should.Throw<OptionsValidationException>(() => Resolve(new Dictionary<string, string?>()));

        ex.Message.ShouldContain("Slskd:Url");
        ex.Message.ShouldContain("Slskd:ApiKey");
    }

    [Theory]
    [InlineData("nas.local:8080")]
    [InlineData("/slskarr")]
    [InlineData("ftp://nas.local")]
    public void Options_reject_non_http_external_url(string url) =>
        Should.Throw<OptionsValidationException>(() => Resolve(Valid(("Slskarr:ExternalUrl", url))))
            .Message.ShouldContain("ExternalUrl");

    [Fact]
    public void Options_accept_http_external_url() =>
        Resolve(Valid(("Slskarr:ExternalUrl", "https://nas.example.org/slskarr"))).ExternalUrl
            .ShouldBe(new Uri("https://nas.example.org/slskarr"));

    [Fact]
    public void Options_reject_unknown_ranking_criterion()
    {
        var ex = Should.Throw<OptionsValidationException>(
            () => Resolve(Valid(("Slskarr:Ebook:Ranking:0", "Foo"))));

        ex.Message.ShouldContain("Foo");
        foreach (var allowed in new[] { "NameMatch", "FreeSlot", "QueueLength", "UploadSpeed", "Format" })
        {
            ex.Message.ShouldContain(allowed);
        }
    }

    [Fact]
    public async Task Host_fails_to_start_when_slskd_config_missing()
    {
        using var factory = new SlskarrFactory().WithConfig(c =>
        {
            c["Slskarr:Slskd:Url"] = "";
            c["Slskarr:Slskd:ApiKey"] = "";
        });

        var ex = await Should.ThrowAsync<OptionsValidationException>(
            () => Task.Run(() => factory.CreateClient()));

        ex.Message.ShouldContain("Slskd:Url");
    }
}
