using Shouldly;
using Slskarr.Profiles.Music;

namespace Slskarr.Tests.Profiles;

public class MusicNameParserTests
{
    [Theory]
    [InlineData(@"Radiohead - OK Computer (1997)", "Radiohead", "OK Computer", 1997)]
    [InlineData(@"Radiohead\1997 - OK Computer", "Radiohead", "OK Computer", 1997)]
    [InlineData(@"Radiohead\OK Computer (1997)", "Radiohead", "OK Computer", 1997)]
    [InlineData(@"Radiohead\(1997) OK Computer", "Radiohead", "OK Computer", 1997)]
    [InlineData(@"Radiohead - 1997 - OK Computer", "Radiohead", "OK Computer", 1997)]
    [InlineData(@"Radiohead - OK Computer [1997]", "Radiohead", "OK Computer", 1997)]
    [InlineData(@"1997 - Radiohead - OK Computer", "Radiohead", "OK Computer", 1997)]
    [InlineData(@"@@abcde\Music\Radiohead\OK Computer", "Radiohead", "OK Computer", null)]
    [InlineData(@"@@abcde\Music\Radiohead - OK Computer [FLAC]", "Radiohead", "OK Computer", null)]
    [InlineData(@"C:\Music\Radiohead - OK_Computer (1997) [320]", "Radiohead", "OK Computer", 1997)]
    public void Parse_extracts_artist_album_year(string path, string artist, string album, int? year)
    {
        var parsed = MusicNameParser.Parse(path);
        parsed.Author.ShouldBe(artist);
        parsed.Title.ShouldBe(album);
        parsed.Year.ShouldBe(year);
        parsed.Format.ShouldBe("");
    }

    [Fact]
    public void Parse_unknown_layout_keeps_only_stem()
    {
        var parsed = MusicNameParser.Parse("random stuff");
        parsed.Author.ShouldBeNull();
        parsed.Title.ShouldBeNull();
        parsed.Stem.ShouldBe("random stuff");
    }

    [Fact]
    public void Parse_stem_is_cleaned_last_folder()
    {
        MusicNameParser.Parse(@"@@abcde\Music\Radiohead\OK Computer (1997) [FLAC]").Stem.ShouldBe("OK Computer");
    }
}
