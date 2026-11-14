namespace Slskarr.Soulseek;

/// <summary>slskd could not be reached, timed out, or answered with an unexpected or server-error status.</summary>
public sealed class SlskdUnavailableException : Exception
{
    public SlskdUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
