using System.Security.Cryptography;
using System.Text;

namespace Slskarr.Auth;

/// <summary>Shared <c>apikey</c> check for the Newznab and SABnzbd faces. With no configured key everything is open.</summary>
public static class ApiKeyCheck
{
    public static bool IsValid(HttpRequest req, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        string? supplied = req.Query["apikey"];
        if (string.IsNullOrEmpty(supplied) && req.HasFormContentType)
        {
            supplied = req.Form["apikey"];
        }

        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        return Matches(supplied, configured);
    }

    /// <summary>Constant-time comparison of a supplied credential against the configured key.</summary>
    public static bool Matches(string supplied, string configured)
    {
        // Hash first so the comparison is constant-time regardless of length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
    }
}
