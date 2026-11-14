using System.Text;
using System.Xml;

namespace Slskarr.Nzb;

/// <summary>Makes peer-supplied text safe for XML 1.0 output: drops invalid chars and lone surrogates, keeps valid pairs.</summary>
internal static class XmlText
{
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s ?? "";
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[++i]);
            }
            else if (!char.IsSurrogate(c) && XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
