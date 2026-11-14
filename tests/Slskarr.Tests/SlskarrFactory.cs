using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Slskarr.Tests;

/// <summary>
/// In-process Slskarr host for endpoint tests. Supplies valid defaults (slskd URL and key, a unique
/// temp data path), a <see cref="FakeTimeProvider"/>, and routes the slskd HttpClient to a test handler.
/// </summary>
public sealed class SlskarrFactory : WebApplicationFactory<Program>
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "slskarr-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _config = new()
    {
        ["Slskarr:Slskd:Url"] = "http://slskd.test",
        ["Slskarr:Slskd:ApiKey"] = "slskd-test-key",
    };
    private Func<HttpMessageHandler>? _slskdHandler;
    private Action<IServiceCollection>? _services;

    public SlskarrFactory()
    {
        _config["Slskarr:DataPath"] = _dataPath;
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Routes the slskd HttpClient to the given handler (WireMock or FakeSlskd).</summary>
    public SlskarrFactory WithSlskdHandler(Func<HttpMessageHandler> handler)
    {
        _slskdHandler = handler;
        return this;
    }

    /// <summary>Applies extra service registrations after the app's own (e.g. a fake ISlskdClient).</summary>
    public SlskarrFactory WithServices(Action<IServiceCollection> configure)
    {
        _services = configure;
        return this;
    }

    /// <summary>Overrides configuration; keys use ':' separators, e.g. "Slskarr:ApiKey".</summary>
    public SlskarrFactory WithConfig(Action<Dictionary<string, string?>> configure)
    {
        configure(_config);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_config));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            if (_slskdHandler is { } handler)
            {
                services.AddHttpClient("slskd").ConfigurePrimaryHttpMessageHandler(handler);
            }

            _services?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }
}
