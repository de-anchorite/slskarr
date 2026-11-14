using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Slskarr.Tests;

public class HealthTests
{
    [Fact]
    public async Task Health_returns_ok()
    {
        using var factory = new SlskarrFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().ShouldBe("ok");
        body.GetProperty("version").GetString().ShouldBe(AppVersion.Current);
    }

    [Fact]
    public void AppVersion_defaults_to_dev_build_without_source_hash()
    {
        // Directory.Build.props sets 0.0.0-dev; release builds override it with -p:Version=<tag>.
        AppVersion.Current.ShouldBe("0.0.0-dev");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("0.1.0-rc.1+deadbeef", "0.1.0-rc.1")]
    [InlineData(null, "0.0.0-dev")]
    [InlineData("", "0.0.0-dev")]
    public void AppVersion_strips_source_revision(string? informational, string expected)
    {
        AppVersion.Normalise(informational).ShouldBe(expected);
    }
}
