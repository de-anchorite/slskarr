namespace Slskarr.Data;

/// <summary>
/// Checks at startup that the data folder can be created and written, so a bad Docker volume
/// gives one clear message instead of a SQLite "unable to open database file" stack trace.
/// </summary>
public static class DataDirectory
{
    private const string ProbeFileName = ".slskarr-write-test";

    /// <summary>Returns null when <paramref name="path"/> is usable, else a message saying how to fix it.</summary>
    public static string? FindProblem(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using (new FileStream(Path.Combine(path, ProbeFileName), FileMode.Create, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var user = CurrentUser();
            return $"Cannot write to the data folder '{path}' as user {user} ({ex.Message}). " +
                   $"If it is a bind-mounted host folder, either make it writable by this user (chown {user} <host folder>), " +
                   "or run the container as the folder's owner with `user: \"<uid>:<gid>\"` in compose (docker run --user <uid>:<gid>).";
        }
    }

    /// <summary>"uid:gid" of this process, read from /proc on Linux; "unknown" elsewhere.</summary>
    private static string CurrentUser()
    {
        try
        {
            string? uid = null, gid = null;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    uid = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                }
                else if (line.StartsWith("Gid:", StringComparison.Ordinal))
                {
                    gid = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                }
            }

            return uid is null || gid is null ? "unknown" : $"{uid}:{gid}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
