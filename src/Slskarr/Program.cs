using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Slskarr;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Downloads;
using Slskarr.Newznab;
using Slskarr.Profiles;
using Slskarr.Profiles.Ebook;
using Slskarr.Sabnzbd;
using Slskarr.Search;
using Slskarr.Soulseek;
using Slskarr.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSlskarrOptions(builder.Configuration);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSlskarrData();
builder.Services.AddSingleton<EbookNameParser>();
builder.Services.AddSingleton<IMediaProfile, EbookProfile>();
builder.Services.AddSingleton<ProfileRegistry>();
builder.Services.AddHttpClient<ISlskdClient, SlskdClient>("slskd", (sp, client) =>
    SlskdClient.Configure(client, sp.GetRequiredService<IOptions<SlskarrOptions>>().Value.Slskd));
builder.Services.AddSingleton<Ranker>();
builder.Services.AddSingleton<SearchThrottle>();
builder.Services.AddSingleton<SearchCoordinator>();
builder.Services.AddHostedService<SearchWorker>();
builder.Services.AddSingleton<FileManagementStatus>();
builder.Services.AddSingleton<DownloadManager>();
builder.Services.AddHostedService<DownloadMonitor>();

var app = builder.Build();

app.Logger.LogInformation("Slskarr {Version} starting", AppVersion.Current);

app.Services.MigrateSlskarrDb();

if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<SlskarrOptions>>().Value.ApiKey))
{
    app.Logger.LogWarning("No API key set; Slskarr endpoints are unauthenticated");
}

app.UseForwardedHeaders();

app.MapGet("/health", () => Results.Json(new { status = "ok", version = AppVersion.Current }));

app.MapNewznab();
app.MapSabnzbd();
app.MapPages();

app.Run();

public partial class Program;
