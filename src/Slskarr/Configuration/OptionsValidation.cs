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
    }
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

        var unknown = options.Ebook.Ranking
            .Where(c => !EbookProfileOptions.AllowedRankingCriteria.Contains(c, StringComparer.Ordinal))
            .ToList();
        if (unknown.Count > 0)
        {
            failures.Add(
                $"Slskarr:Ebook:Ranking contains unknown criteria ({string.Join(", ", unknown)}). " +
                $"Allowed: {string.Join(", ", EbookProfileOptions.AllowedRankingCriteria)}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
