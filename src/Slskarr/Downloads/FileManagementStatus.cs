namespace Slskarr.Downloads;

/// <summary>Process-wide flags the status page reads.</summary>
public sealed class FileManagementStatus
{
    private int _forbidden;

    /// <summary>True once slskd answered 403 to a file delete (remote_file_management is off).</summary>
    public bool RemoteDeleteForbidden => Volatile.Read(ref _forbidden) == 1;

    /// <summary>Sets the flag; returns true only for the first call in this process.</summary>
    public bool MarkRemoteDeleteForbidden() => Interlocked.Exchange(ref _forbidden, 1) == 0;
}
