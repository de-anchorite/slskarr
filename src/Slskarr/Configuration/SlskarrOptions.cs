namespace Slskarr.Configuration;

/// <summary>Root options, bound from the <c>Slskarr</c> configuration section.</summary>
/// <remarks>
/// Array defaults are applied in <see cref="OptionsValidation"/> PostConfigure rather than via
/// initialisers: the configuration binder appends to pre-populated arrays, so configured values
/// would otherwise be added to the defaults instead of replacing them.
/// </remarks>
public sealed class SlskarrOptions
{
    public const string SectionName = "Slskarr";

    public string? ApiKey { get; set; }

    /// <summary>
    /// Base URL a browser uses to reach Slskarr, for links the *arr UIs display (release info pages). Unset uses
    /// the URL the request came in on (honouring <c>X-Forwarded-*</c>). NZB download links always use the latter.
    /// </summary>
    public Uri? ExternalUrl { get; set; }

    public string DataPath { get; set; } = "/data";

    public SlskdOptions Slskd { get; set; } = new();

    public PathOptions Paths { get; set; } = new();

    public SearchOptions Search { get; set; } = new();

    public DownloadOptions Downloads { get; set; } = new();

    public EbookProfileOptions Ebook { get; set; } = new();

    public MusicProfileOptions Music { get; set; } = new();
}

public sealed class SlskdOptions
{
    public Uri? Url { get; set; }

    public string ApiKey { get; set; } = "";

    public string DownloadsPath { get; set; } = "/downloads";
}

public sealed class PathOptions
{
    public string? StorageRewriteFrom { get; set; }

    public string? StorageRewriteTo { get; set; }
}

public sealed class SearchOptions
{
    public const int MinSlskdSearchTimeoutSeconds = 5;

    public TimeSpan SyncWait { get; set; } = TimeSpan.FromSeconds(60);

    public int Concurrency { get; set; } = 1;

    public TimeSpan MinGap { get; set; } = TimeSpan.FromSeconds(10);

    public int HourlyCap { get; set; } = 30;

    public int ResultCap { get; set; } = 50;

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan QueuedExpiry { get; set; } = TimeSpan.FromHours(24);

    public int SlskdSearchTimeoutSeconds { get; set; } = 15;

    public int ResponseLimit { get; set; } = 200;

    public TimeSpan RssWindow { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
}

public sealed class DownloadOptions
{
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class EbookProfileOptions
{
    public static readonly string[] AllowedRankingCriteria =
        ["NameMatch", "FreeSlot", "QueueLength", "UploadSpeed", "Format"];

    public static readonly string[] DefaultExtensions =
        ["epub", "azw3", "azw", "mobi", "pdf", "fb2", "djvu", "cbz", "cbr"];

    public static readonly string[] DefaultFormatPreference =
        ["epub", "azw3", "mobi", "azw", "pdf", "fb2", "djvu", "cbz", "cbr"];

    public string SabCategory { get; set; } = "ebooks";

    /// <summary>Defaults to <see cref="DefaultExtensions"/> when not configured.</summary>
    public string[] Extensions { get; set; } = [];

    /// <summary>Defaults to <see cref="DefaultFormatPreference"/> when not configured.</summary>
    public string[] FormatPreference { get; set; } = [];

    public long? MinSizeBytes { get; set; } = 10_240;

    public long? MaxSizeBytes { get; set; } = 209_715_200;

    /// <summary>Defaults to <see cref="AllowedRankingCriteria"/> order when not configured.</summary>
    public string[] Ranking { get; set; } = [];
}

public sealed class MusicProfileOptions
{
    public static readonly string[] DefaultExtensions =
        ["flac", "mp3", "m4a", "aac", "ogg", "opus", "wav", "aiff", "aif", "ape", "wv", "alac"];

    public static readonly string[] DefaultFormatPreference =
        ["flac", "mp3", "m4a", "alac", "ogg", "opus", "aac", "wav", "aiff", "aif", "ape", "wv"];

    public string SabCategory { get; set; } = "music";

    /// <summary>Defaults to <see cref="DefaultExtensions"/> when not configured.</summary>
    public string[] Extensions { get; set; } = [];

    /// <summary>Defaults to <see cref="DefaultFormatPreference"/> when not configured.</summary>
    public string[] FormatPreference { get; set; } = [];

    /// <summary>Minimum total size of a release (all files), or null for no limit.</summary>
    public long? MinSizeBytes { get; set; }

    /// <summary>Maximum total size of a release (all files), or null for no limit.</summary>
    public long? MaxSizeBytes { get; set; }

    /// <summary>Defaults to <see cref="EbookProfileOptions.AllowedRankingCriteria"/> order when not configured.</summary>
    public string[] Ranking { get; set; } = [];
}
