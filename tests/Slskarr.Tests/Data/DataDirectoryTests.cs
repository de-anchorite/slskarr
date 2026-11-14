using Shouldly;
using Slskarr.Data;

namespace Slskarr.Tests.Data;

public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "slskarr-datadir-tests", Guid.NewGuid().ToString("N"));

    public DataDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Writable_folder_has_no_problem_and_leaves_no_probe_file()
    {
        var path = Path.Combine(_root, "data");

        DataDirectory.FindProblem(path).ShouldBeNull();

        Directory.Exists(path).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(path).ShouldBeEmpty();
    }

    [Fact]
    public void Folder_that_cannot_be_created_reports_path_and_fix()
    {
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "x");
        var path = Path.Combine(file, "data");

        var problem = DataDirectory.FindProblem(path);

        problem.ShouldNotBeNull();
        problem.ShouldContain(path);
        problem.ShouldContain("user:");
    }

    [Fact]
    public void Read_only_folder_reports_path_user_and_fix()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // Unix permissions only; root ignores them. CI and the Docker image run on Linux as non-root.
        }

        var path = Path.Combine(_root, "readonly");
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var problem = DataDirectory.FindProblem(path);

        problem.ShouldNotBeNull();
        problem.ShouldContain(path);
        problem.ShouldContain("chown");
        problem.ShouldContain("user:");
        problem.ShouldMatch(@"user \d+:\d+");
    }
}
