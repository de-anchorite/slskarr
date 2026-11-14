namespace Slskarr.Profiles;

/// <summary>Result of parsing a Soulseek file path. <c>Format</c> is the upper-case extension; <c>Stem</c> the cleaned name without extension.</summary>
public sealed record ParsedName(string? Author, string? Title, int? Year, string Format, string Stem);
