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
}
