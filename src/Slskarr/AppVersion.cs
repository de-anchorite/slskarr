using System.Reflection;

namespace Slskarr;

/// <summary>
/// Slskarr's own version. Release builds set it from the git tag (<c>-p:Version=1.2.3</c>, see the
/// Dockerfile and CI); local builds report <c>0.0.0-dev</c> from Directory.Build.props.
/// </summary>
public static class AppVersion
{
    private const string Dev = "0.0.0-dev";

    public static string Current { get; } = Normalise(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Drops any <c>+sourcerevision</c> suffix; empty means a dev build.</summary>
    public static string Normalise(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return Dev;
        }

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
