namespace Slskarr.Soulseek;

/// <summary>Helpers for Soulseek remote paths, which use backslashes (and sometimes slashes).</summary>
public static class SoulseekPath
{
    private static readonly char[] Separators = ['\\', '/'];

    public static IReadOnlyList<string> Segments(string remotePath) =>
        remotePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    public static string FileName(string remotePath)
    {
        var segments = Segments(remotePath);
        return segments.Count == 0 ? string.Empty : segments[^1];
    }

    /// <summary>Lower-case extension without the dot, or empty when there is none.</summary>
    public static string Extension(string remotePath)
    {
        var name = FileName(remotePath);
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }
}
