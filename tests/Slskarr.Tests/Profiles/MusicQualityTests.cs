using Shouldly;
using Slskarr.Profiles;
using Slskarr.Profiles.Music;

namespace Slskarr.Tests.Profiles;

public class MusicQualityTests
{
    private static List<CandidateFile> Files(string ext, int n, int? bitRate = null, int? depth = null, int? rate = null, bool? vbr = null) =>
        Enumerable.Range(1, n)
            .Select(i => new CandidateFile($@"Radiohead\OK Computer\{i:00}.{ext}", 1000, "", bitRate, depth, rate, 200, vbr))
            .ToList();

    public static TheoryData<string, List<CandidateFile>, string, string, string> Cases => new()
    {
        { "FLAC", Files("flac", 3, 900, 16, 44100), "OK Computer", "FLAC", "FLAC" },
        { "FLAC", Files("flac", 3, 2300, 24, 96000), "OK Computer", "FLAC 24bit", "FLAC 24bit" },
        { "MP3", Files("mp3", 3, 320, null, 44100, false), "OK Computer", "MP3 320", "MP3-320" },
        { "MP3", Files("mp3", 3, 247, null, 44100, true), "OK Computer", "MP3 VBR V0", "MP3-VBR-V0" },
        { "MP3", Files("mp3", 3, 190, null, 44100, true), "OK Computer", "MP3 VBR V2", "MP3-VBR-V2" },
        { "MP3", Files("mp3", 3, 190, null, 44100, false), "OK Computer", "MP3 192", "MP3-192" },
        { "MP3", Files("mp3", 3), "OK Computer", "MP3", "Unknown" },
        { "MP3", Files("mp3", 3), "OK Computer [320]", "MP3 320", "MP3-320" },
        { "MP3", Files("mp3", 3), "OK Computer [V0]", "MP3 VBR V0", "MP3-VBR-V0" },
        { "FLAC", Files("flac", 3), "OK Computer [24bit]", "FLAC 24bit", "FLAC 24bit" },
        { "FLAC", Files("flac", 3), "OK Computer", "FLAC", "FLAC" },
        { "M4A", Files("m4a", 3, 256, null, 44100, false), "OK Computer", "AAC 256", "AAC-256" },
        { "APE", Files("ape", 3, 900), "OK Computer", "APE", "APE" },
        { "OPUS", Files("opus", 3), "OK Computer", "Opus", "Unknown" },
        { "OPUS", Files("opus", 3, 192), "OK Computer", "Opus 192", "Vorbis-Q6" },
        { "OGG", Files("ogg", 3, 320), "OK Computer", "OGG 320", "Vorbis-Q9" },
        { "OGG", Files("ogg", 3, 128), "OK Computer", "OGG", "Unknown" },
        { "OGG", Files("ogg", 3, 200), "OK Computer", "OGG 192", "Vorbis-Q6" },
        { "OPUS", Files("opus", 3, 128), "OK Computer", "Opus", "Unknown" },
        { "MP3", Files("mp3", 3, 318, null, 44100, false), "OK Computer", "MP3 320", "MP3-320" },
        { "MP3", Files("mp3", 3, 64, null, 44100, false), "OK Computer", "MP3", "Unknown" },
        { "MP3", Files("mp3", 3, 224, null, 44100, false), "OK Computer", "MP3 192", "MP3-192" },
        { "MP3", Files("mp3", 3, 240, null, 44100, false), "OK Computer", "MP3 192", "MP3-192" },
        { "M4A", Files("m4a", 3, 128, null, 44100, false), "OK Computer", "AAC", "AAC-VBR" },
        { "AIFF", Files("aiff", 3), "OK Computer", "AIFF", "Unknown" },
        { "ALAC", Files("m4a", 3, 900, 24, 96000), "OK Computer", "ALAC 24bit", "ALAC 24bit" },
        { "WAV", Files("wav", 3), "OK Computer", "WAV", "WAV" },
        { "WV", Files("wv", 3), "OK Computer", "WV", "WavPack" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Tag_matches_intended_lidarr_quality(string format, List<CandidateFile> files, string folder, string tag, string lidarr)
    {
        MusicQuality.Tag(format, files, folder).ShouldBe(tag);
        var title = MusicTitle.Build(MusicNameParser.Parse(@"Radiohead\OK Computer (1997)"), tag);
        title.ShouldBe($"Radiohead - OK Computer (1997) [{tag}]");
        LidarrQualityParser.Parse(title).ShouldBe(lidarr);
    }

    [Theory]
    [InlineData(@"Radiohead - OK Computer FLAC (1997)", "FLAC", "FLAC")]
    [InlineData(@"Artist - Album 320kbps", "MP3 VBR V0", "MP3-VBR-V0")]
    [InlineData(@"Artist - Album V0 WEB", "MP3 320", "MP3-320")]
    [InlineData(@"Artist - Album MP3 V2", "MP3 256", "MP3-256")]
    [InlineData(@"Artist - Album [24bit] 24-96 (2001)", "FLAC", "FLAC")]
    [InlineData(@"Artist\Album FLAC 24bit 2001", "FLAC 24bit", "FLAC 24bit")]
    public void Contaminated_names_do_not_override_tag(string folder, string tag, string lidarr)
    {
        var title = MusicTitle.Build(MusicNameParser.Parse(folder), tag);
        LidarrQualityParser.Parse(title).ShouldBe(lidarr);
    }

    [Fact]
    public void Tag_uses_modal_bitrate_and_majority_bit_depth()
    {
        var files = Files("mp3", 2, 320, null, null, false).Concat(Files("mp3", 1, 128, null, null, false)).ToList();
        MusicQuality.Tag("MP3", files, "x").ShouldBe("MP3 320");
        var flac = Files("flac", 1, null, 24).Concat(Files("flac", 2, null, 16)).ToList();
        MusicQuality.Tag("FLAC", flac, "x").ShouldBe("FLAC");
    }

    [Theory]
    [InlineData("Radiohead", "OK Computer", 1997, "FLAC", "Radiohead - OK Computer (1997) [FLAC]")]
    [InlineData("Radiohead", "OK Computer", null, "MP3 320", "Radiohead - OK Computer [MP3 320]")]
    [InlineData(null, null, null, "FLAC", "random stuff [FLAC]")]
    public void Title_builds(string? artist, string? album, int? year, string tag, string expected)
    {
        MusicTitle.Build(new Slskarr.Profiles.ParsedName(artist, album, year, "", "random stuff"), tag).ShouldBe(expected);
    }
}
