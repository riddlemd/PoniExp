using System.Text.RegularExpressions;

namespace PoniExp.Core.Protocol;

public static partial class Limits
{
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxKindLength = 64;
    public const int MaxDescriptionField = 256;

    /// <summary>How far a signed timestamp may be from the router's clock.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>Lowercase letters, digits and dashes, starting with a letter: e.g. "ise", "wojtek".</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static bool IsValidKind(string? kind) => kind is not null && KindPattern().IsMatch(kind);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z][a-z0-9._/-]{0,63}$")]
    private static partial Regex KindPattern();
}
