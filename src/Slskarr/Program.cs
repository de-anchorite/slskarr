using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;
using Slskarr.Data;
using Slskarr.Profiles;
using Slskarr.Profiles.Ebook;
using Slskarr.Soulseek;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSlskarrOptions(builder.Configuration);
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSlskarrData();
builder.Services.AddSingleton<EbookNameParser>();
builder.Services.AddSingleton<IMediaProfile, EbookProfile>();
builder.Services.AddSingleton<ProfileRegistry>();
builder.Services.AddHttpClient<ISlskdClient, SlskdClient>("slskd", (sp, client) =>
    SlskdClient.Configure(client, sp.GetRequiredService<IOptions<SlskarrOptions>>().Value.Slskd));

var app = builder.Build();

app.Services.MigrateSlskarrDb();

if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<SlskarrOptions>>().Value.ApiKey))
{
    app.Logger.LogWarning("No API key set; Slskarr endpoints are unauthenticated");
}

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.Run();

public partial class Program;
