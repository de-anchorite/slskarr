using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSlskarrOptions(builder.Configuration);
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("slskd", (sp, client) =>
{
    var slskd = sp.GetRequiredService<IOptions<SlskarrOptions>>().Value.Slskd;
    client.BaseAddress = slskd.Url;
    client.DefaultRequestHeaders.Add("X-API-Key", slskd.ApiKey);
});

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<SlskarrOptions>>().Value.ApiKey))
{
    app.Logger.LogWarning("No API key set; Slskarr endpoints are unauthenticated");
}

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.Run();

public partial class Program;
