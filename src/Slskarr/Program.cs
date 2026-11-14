using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Newznab;
using Slskarr.Profiles;
using Slskarr.Profiles.Ebook;
using Slskarr.Search;
using Slskarr.Soulseek;

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

var app = builder.Build();

app.Services.MigrateSlskarrDb();

if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<SlskarrOptions>>().Value.ApiKey))
{
    app.Logger.LogWarning("No API key set; Slskarr endpoints are unauthenticated");
}

app.UseForwardedHeaders();

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.MapNewznab();

app.Run();

public partial class Program;
