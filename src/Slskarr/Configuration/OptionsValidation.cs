using Microsoft.Extensions.Options;

namespace Slskarr.Configuration;

public static class OptionsValidation
{
    public static IServiceCollection AddSlskarrOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SlskarrOptions>()
            .Bind(configuration.GetSection(SlskarrOptions.SectionName))
            .PostConfigure(ApplyDefaults)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SlskarrOptions>, SlskarrOptionsValidator>();
        return services;
    }

    private static void ApplyDefaults(SlskarrOptions options)
    {
        var ebook = options.Ebook;

        // Compared with extensions parsed from Soulseek file names: lower case, no dot.
        ebook.Extensions = NormaliseExtensions(ebook.Extensions);
        ebook.FormatPreference = NormaliseExtensions(ebook.FormatPreference);
        if (ebook.Extensions.Length == 0)
        {
            ebook.Extensions = [.. EbookProfileOptions.DefaultExtensions];
        }
        if (ebook.FormatPreference.Length == 0)
        {
            ebook.FormatPreference = [.. EbookProfileOptions.DefaultFormatPreference];
        }
        if (ebook.Ranking.Length == 0)
        {
            ebook.Ranking = [.. EbookProfileOptions.AllowedRankingCriteria];
        }

        ApplyMusicDefaults(options.Music);
    }

    private static void ApplyMusicDefaults(MusicProfileOptions music)
    {
        music.Extensions = NormaliseExtensions(music.Extensions);
        music.FormatPreference = NormaliseExtensions(music.FormatPreference);
        if (music.Extensions.Length == 0)
        {
            music.Extensions = [.. MusicProfileOptions.DefaultExtensions];
        }
        if (music.FormatPreference.Length == 0)
        {
            music.FormatPreference = [.. MusicProfileOptions.DefaultFormatPreference];
        }
        if (music.Ranking.Length == 0)
        {
            music.Ranking = [.. EbookProfileOptions.AllowedRankingCriteria];
        }
    }

    private static string[] NormaliseExtensions(string[] extensions) =>
    [
        .. extensions
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0),
    ];
}

public sealed class SlskarrOptionsValidator : IValidateOptions<SlskarrOptions>
{
    public ValidateOptionsResult Validate(string? name, SlskarrOptions options)
    {
        var failures = new List<string>();

        if (options.Slskd.Url is not { IsAbsoluteUri: true } url
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Slskarr:Slskd:Url is required and must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(options.Slskd.ApiKey))
        {
            failures.Add("Slskarr:Slskd:ApiKey is required.");
        }

        if (options.Search.SlskdSearchTimeoutSeconds < SearchOptions.MinSlskdSearchTimeoutSeconds)
        {
            failures.Add($"Slskarr:Search:SlskdSearchTimeoutSeconds must be at least {SearchOptions.MinSlskdSearchTimeoutSeconds}.");
        }

        var unknown = options.Ebook.Ranking
            .Where(c => !EbookProfileOptions.AllowedRankingCriteria.Contains(c, StringComparer.Ordinal))
            .ToList();
        if (unknown.Count > 0)
        {
            failures.Add(
                $"Slskarr:Ebook:Ranking contains unknown criteria ({string.Join(", ", unknown)}). " +
                $"Allowed: {string.Join(", ", EbookProfileOptions.AllowedRankingCriteria)}.");
        }

        var unknownMusic = options.Music.Ranking
            .Where(c => !EbookProfileOptions.AllowedRankingCriteria.Contains(c, StringComparer.Ordinal))
            .ToList();
        if (unknownMusic.Count > 0)
        {
            failures.Add(
                $"Slskarr:Music:Ranking contains unknown criteria ({string.Join(", ", unknownMusic)}). " +
                $"Allowed: {string.Join(", ", EbookProfileOptions.AllowedRankingCriteria)}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
