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
    }
}
