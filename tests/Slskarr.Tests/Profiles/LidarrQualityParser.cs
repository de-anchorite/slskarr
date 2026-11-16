using System.Text.RegularExpressions;

namespace Slskarr.Tests.Profiles;

/// <summary>
/// Test-only, minimal port of the title-based part of Lidarr's quality detection: the codec, bit rate and
/// sample-size regexes and the codec/bit rate to quality mapping of
/// Lidarr/Lidarr (develop) src/NzbDrone.Core/Parser/QualityParser.cs (<c>ParseQuality</c>, <c>ParseCodec</c>,
/// <c>ParseBitRate</c>, <c>ParseSampleSize</c>). Lidarr is GPL-3; this port lives only in the test project.
/// Returns Lidarr's quality name (<c>FLAC</c>, <c>FLAC 24bit</c>, <c>MP3-320</c>, <c>MP3-VBR-V0</c>, ...), or
/// <c>Unknown</c>. Omitted: tag-lib description path, extension fallback, proper/repack/real modifiers.
/// </summary>
public static partial class LidarrQualityParser
{
    [GeneratedRegex(@"\b(?:(?<B096>96[ ]?kbps|96|[\[\(].*96.*[\]\)])|
                                                                (?<B128>128[ ]?kbps|128|[\[\(].*128.*[\]\)])|
                                                                (?<B160>160[ ]?kbps|160|[\[\(].*160.*[\]\)]|q5)|
                                                                (?<B192>192[ ]?kbps|192|[\[\(].*192.*[\]\)]|q6)|
                                                                (?<B224>224[ ]?kbps|224|[\[\(].*224.*[\]\)]|q7)|
                                                                (?<B256>256[ ]?kbps|256|itunes\splus|[\[\(].*256.*[\]\)]|q8)|
                                                                (?<B320>320[ ]?kbps|320|[\[\(].*320.*[\]\)]|q9)|
                                                                (?<B500>500[ ]?kbps|500|[\[\(].*500.*[\]\)]|q10)|
                                                                (?<VBRV0>V0[ ]?kbps|V0|[\[\(].*V0.*[\]\)])|
                                                                (?<VBRV2>V2[ ]?kbps|V2|[\[\(].*V2.*[\]\)]))\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex BitRateRegex();

    [GeneratedRegex(@"\b(?:(?<S24>24[-._ ]?bit|flac24(?:[-._ ]?bit)?|tr24|24-(?:44|48|96|192)|[\[\(].*24bit.*[\]\)]))\b")]
    private static partial Regex SampleSizeRegex();

    [GeneratedRegex(@"\b(?:(?<MP1>MPEG Version \d(.5)? Audio, Layer 1|MP1)|(?<MP2>MPEG Version \d(.5)? Audio, Layer 2|MP2)|(?<MP3VBR>MP3.*VBR|MPEG Version \d(.5)? Audio, Layer 3 vbr)|(?<MP3CBR>MP3|MPEG Version \d(.5)? Audio, Layer 3)|(?<FLAC>(web)?flac(?:24(?:[-._ ]?bit)?)?|TR24)|(?<WAVPACK>wavpack|wv)|(?<ALAC>alac)|(?<WMA>WMA\d?)|(?<WAV>WAV|PCM)|(?<AAC>M4A|M4P|M4B|AAC|mp4a|MPEG-4 Audio(?!.*alac))|(?<OGG>OGG|OGA|Vorbis))\b|(?<APE>monkey's audio|[\[|\(].*\bape\b.*[\]|\)])|(?<OPUS>Opus Version \d(.5)? Audio|[\[|\(].*\bopus\b.*[\]|\)])",
        RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"\b(?<web>WEB)(?:\b|$|[ .])", RegexOptions.IgnoreCase)]
    private static partial Regex WebRegex();

    public static string Parse(string title)
    {
        var name = title.Replace('_', ' ').Trim().ToLowerInvariant();
        var codec = CodecRegex().Match(name);
        var bitrate = BitRateRegex().Match(name);
        var s24 = SampleSizeRegex().Match(name).Success;

        bool Rate(string group) => bitrate.Success && bitrate.Groups[group].Success;
        bool Codec(string group) => codec.Success && codec.Groups[group].Success;

        // Same precedence as Lidarr's ParseCodec.
        if (Codec("FLAC")) return s24 ? "FLAC 24bit" : "FLAC";
        if (Codec("ALAC")) return s24 ? "ALAC 24bit" : "ALAC";
        if (Codec("WMA")) return "WMA";
        if (Codec("WAV")) return "WAV";
        if (Codec("AAC"))
        {
            if (Rate("B192")) return "AAC-192";
            if (Rate("B256")) return "AAC-256";
            if (Rate("B320")) return "AAC-320";
            return "AAC-VBR";
        }

        if (Codec("OGG") || Codec("OPUS"))
        {
            if (Rate("B160")) return "Vorbis-Q5";
            if (Rate("B192")) return "Vorbis-Q6";
            if (Rate("B224")) return "Vorbis-Q7";
            if (Rate("B256")) return "Vorbis-Q8";
            if (Rate("B320")) return "Vorbis-Q9";
            if (Rate("B500")) return "Vorbis-Q10";
            return "Unknown";
        }

        if (Codec("MP1") || Codec("MP2")) return "Unknown";
        if (Codec("MP3VBR"))
        {
            if (Rate("VBRV0")) return "MP3-VBR-V0";
            if (Rate("VBRV2")) return "MP3-VBR-V2";
            return "Unknown";
        }

        if (Codec("MP3CBR"))
        {
            if (Rate("B096")) return "MP3-96";
            if (Rate("B128")) return "MP3-128";
            if (Rate("B160")) return "MP3-160";
            if (Rate("B192")) return "MP3-192";
            if (Rate("B256")) return "MP3-256";
            if (Rate("B320")) return "MP3-320";
            return "Unknown";
        }

        if (Codec("WAVPACK")) return "WavPack";
        if (Codec("APE")) return "APE";

        // Codec.Unknown
        if (Rate("B192")) return "MP3-192";
        if (Rate("B256")) return "MP3-256";
        if (Rate("B320")) return "MP3-320";
        return WebRegex().IsMatch(name) ? "MP3-320" : "Unknown";
    }
}
