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
        { "OPUS", Files("opus", 3, 128), "OK Computer", "Opus", "Unknown" },
        { "ALAC", Files("m4a", 3, 900, 24, 96000), "OK Computer", "ALAC 24bit", "ALAC 24bit" },
        { "WAV", Files("wav", 3), "OK Computer", "WAV", "WAV" },
        { "WV", Files("wv", 3), "OK Computer", "WV", "WavPack" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Tag_matches_intended_lidarr_quality(string format, List<CandidateFile> files, string folder, string tag, string lidarr)
    {
        MusicQuality.Tag(format, files, folder).ShouldBe(tag);
        LidarrQualityParser.Parse($"Radiohead - OK Computer (1997) [{tag}]").ShouldBe(lidarr);
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
