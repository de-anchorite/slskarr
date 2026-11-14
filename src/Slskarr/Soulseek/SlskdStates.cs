namespace Slskarr.Soulseek;

/// <summary>
/// Classifies slskd transfer state strings (Soulseek.NET <c>TransferStates</c> flags rendered by
/// <c>Enum.ToString</c>, e.g. "Queued, Remotely"), mirroring slskd's <c>TransferStateCategories</c>.
/// </summary>
public static class SlskdStates
{
    private static readonly HashSet<string> FailureFlags = ["Cancelled", "TimedOut", "Errored", "Rejected", "Aborted"];

    public static bool IsQueued(string state)
    {
        var flags = Parse(state);
        return !flags.Contains("Completed")
            && (flags.Contains("Requested") || flags.Contains("Queued"))
            && !flags.Contains("Initializing")
            && !flags.Contains("InProgress");
    }

    public static bool IsInProgress(string state)
    {
        var flags = Parse(state);
        return !flags.Contains("Completed") && (flags.Contains("Initializing") || flags.Contains("InProgress"));
    }

    public static bool IsSucceeded(string state)
    {
        var flags = Parse(state);
        return flags.Contains("Completed") && flags.Contains("Succeeded");
    }

    /// <summary>True for Completed plus a failure flag; <paramref name="reason"/> is the part after "Completed, ".</summary>
    public static bool IsFailed(string state, out string reason)
    {
        var parts = Split(state);
        if (parts.Contains("Completed") && parts.Any(FailureFlags.Contains))
        {
            reason = string.Join(", ", parts.Where(p => p != "Completed"));
            return true;
        }

        reason = "";
        return false;
    }

    private static string[] Split(string? state) =>
        (state ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static HashSet<string> Parse(string? state) => [.. Split(state)];
}
