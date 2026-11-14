using Shouldly;
using Slskarr.Configuration;
using Slskarr.Downloads;

namespace Slskarr.Tests.Downloads;

public sealed class StoragePathsTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Storage_joins_downloads_job_dir_and_file_name()
    {
        var path = StoragePaths.Storage(new SlskdOptions { DownloadsPath = "/downloads" }, new PathOptions(), Id, @"@@a\Books\Dune.epub");

        path.ShouldBe("/downloads/slskarr/11111111222233334444555555555555/Dune.epub");
    }

    [Fact]
    public void Storage_applies_prefix_rewrite()
    {
        var paths = new PathOptions { StorageRewriteFrom = "/downloads", StorageRewriteTo = "/data/slsk" };

        StoragePaths.Storage(new SlskdOptions { DownloadsPath = "/downloads/" }, paths, Id, @"@@a\Dune.epub")
            .ShouldBe("/data/slsk/slskarr/11111111222233334444555555555555/Dune.epub");
    }

    [Fact]
    public void Storage_ignores_rewrite_when_prefix_differs()
    {
        var paths = new PathOptions { StorageRewriteFrom = "/other", StorageRewriteTo = "/x" };

        StoragePaths.Storage(new SlskdOptions { DownloadsPath = "/downloads" }, paths, Id, "a\\b.epub")
            .ShouldStartWith("/downloads/");
    }

    [Fact]
    public void JobDir_is_relative()
    {
        StoragePaths.JobDir(Id).ShouldBe("slskarr/11111111222233334444555555555555");
    }

    [Theory]
    [InlineData("peer", @"@@abcde\Books\Frank Herbert\Dune.epub", "peer/Books/Frank Herbert/Dune.epub")]
    [InlineData("peer", "@@abcde/Books/Dune.epub", "peer/Books/Dune.epub")]
    [InlineData("peer", @"C:\Users\me\Books\Dune.epub", "peer/Users/me/Books/Dune.epub")]
    [InlineData("peer", @"c:Books\Dune.epub", "peer/Books/Dune.epub")]
    [InlineData("peer", @"\\srv\share\Books\Dune.epub", "peer/share/Books/Dune.epub")]
    [InlineData("peer", "//srv/share/Dune.epub", "peer/share/Dune.epub")]
    [InlineData("peer", @"@@abc\Books\Dune.epub", "peer/@@abc/Books/Dune.epub")] // too short for a Soulseek Qt root
    [InlineData("peer", @"@@abcde\Dune.epub", "peer/Dune.epub")]
    [InlineData("peer", "Dune.epub", "peer/Dune.epub")]
    [InlineData("peer", @"@@abcde\Books\\Dune.epub", "peer/Books/Dune.epub")]
    [InlineData("a/b", @"@@abcde\Books\Dune.epub", "a_b/Books/Dune.epub")]
    [InlineData(@"a\b", @"@@abcde\Books\Dune.epub", "a_b/Books/Dune.epub")]
    [InlineData("..", @"@@abcde\Books\Dune.epub", "_/Books/Dune.epub")]
    [InlineData("peer", @"@@abcde\..\.\Dune.epub", "peer/_/_/Dune.epub")]
    [InlineData("peer", "@@abcde\\Bo\0oks\\Dü\0ne.epub", "peer/Bo_oks/Dü_ne.epub")]
    [InlineData("peer", @"@@abcde\Книги\Достоевский.epub", "peer/Книги/Достоевский.epub")]
    public void IncompleteFile_follows_slskd_layout(string username, string remotePath, string expected)
    {
        StoragePaths.IncompleteFile(username, remotePath).ShouldBe(expected);
    }

    [Theory]
    [InlineData("@@abcde")]
    [InlineData(@"@@abcde\")]
    [InlineData(@"C:\")]
    [InlineData(@"\\srv\")]
    [InlineData(@"@@abcde\Books\")]
    [InlineData("")]
    public void IncompleteFile_is_null_when_there_is_no_file_name(string remotePath)
    {
        StoragePaths.IncompleteFile("peer", remotePath).ShouldBeNull();
    }
}
