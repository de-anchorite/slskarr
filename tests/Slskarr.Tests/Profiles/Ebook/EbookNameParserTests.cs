using Shouldly;
using Slskarr.Profiles.Ebook;
using Slskarr.Soulseek;

namespace Slskarr.Tests.Profiles.Ebook;

public class EbookNameParserTests
{
    private readonly EbookNameParser _parser = new();

    [Theory]
    [InlineData(@"@@abcde\Books\Ursula K. Le Guin - The Dispossessed (1974) [EPUB].epub", "Ursula K. Le Guin", "The Dispossessed", 1974, "EPUB")]
    [InlineData(@"music\ebooks\Terry Pratchett\Discworld\05 - Sourcery.epub", "Terry Pratchett", "Sourcery", null, "EPUB")]
    [InlineData(@"Terry Pratchett\05. Sourcery.epub", "Terry Pratchett", "Sourcery", null, "EPUB")]
    [InlineData("share/Calibre Library/Iain M. Banks/Excession (123)/Excession - Iain M. Banks.azw3", "Iain M. Banks", "Excession", null, "AZW3")]
    [InlineData(@"x\Dune by Frank Herbert.mobi", "Frank Herbert", "Dune", null, "MOBI")]
    [InlineData(@"x\Books\Frank_Herbert_-_Dune_ISBN_9780441013593.pdf", "Frank Herbert", "Dune", null, "PDF")]
    [InlineData(@"x\Books\Frank Herbert - Dune [ISBN 0441013597] (retail).epub", "Frank Herbert", "Dune", null, "EPUB")]
    [InlineData(@"x\Books\Frank Herbert - Dune - 1965.epub", "Frank Herbert", "Dune", 1965, "EPUB")]
    [InlineData(@"x\Books\George Orwell - 1984.epub", "George Orwell", "1984", null, "EPUB")]
    [InlineData(@"x\Books\Frank Herbert\Dune.epub", "Frank Herbert", "Dune", null, "EPUB")]
    [InlineData(@"x\Books\random-scan-0042.pdf", null, "random-scan-0042", null, "PDF")]
    public void Parse_extracts_author_title_year_format(string path, string? author, string title, int? year, string format)
    {
        var parsed = _parser.Parse(path);

        parsed.Author.ShouldBe(author);
        parsed.Title.ShouldBe(title);
        parsed.Year.ShouldBe(year);
        parsed.Format.ShouldBe(format);
    }

    [Fact]
    public void Parse_backslash_paths()
    {
        const string mixed = @"@@abcde\Books/Author\Title.epub";
        SoulseekPath.Segments(mixed).ShouldBe(["@@abcde", "Books", "Author", "Title.epub"]);
        SoulseekPath.Segments(@"@@abcde\Books\Author\Title.epub").ShouldBe(SoulseekPath.Segments(mixed));
        SoulseekPath.FileName(mixed).ShouldBe("Title.epub");
        SoulseekPath.Extension(mixed).ShouldBe("epub");
        SoulseekPath.Segments(@"\\a\\b/").ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Parse_keeps_unicode()
    {
        var ru = _parser.Parse(@"x\Фёдор Достоевский - Преступление и наказание.epub");
        ru.Author.ShouldBe("Фёдор Достоевский");
        ru.Title.ShouldBe("Преступление и наказание");

        _parser.Parse(@"x\Émile Zola - Germinal.epub").Author.ShouldBe("Émile Zola");
    }
}
