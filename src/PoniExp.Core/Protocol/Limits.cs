using System.Text.RegularExpressions;

namespace PoniExp.Core.Protocol;

public static partial class Limits
{
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxDescriptionField = 256;
    public const int MaxSubjectLength = 256;
    public const int MaxContentTypeLength = 128;
    public const int MaxMetadataEntries = 32;
    public const int MaxMetadataKeyLength = 64;
    public const int MaxMetadataValueLength = 1024;

    /// <summary>The largest content (in its signed form) the router accepts: the body plus room for the other fields.</summary>
    public const int MaxContentBytes = MaxBodyBytes + 64 * 1024;

    public const string EndpointRule = "1-64 lowercase letters, digits, '.', '_' or '-', starting with a letter or digit";

    /// <summary>Lowercase letters, digits and dashes, starting with a letter: e.g. "ise", "wojtek".</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static bool IsValidEndpoint(string? endpoint) => endpoint is not null && EndpointPattern().IsMatch(endpoint);

    public static bool IsValidMetadataKey(string? key) => key is not null && MetadataKeyPattern().IsMatch(key);

    /// <summary>A MIME-style type: "text/plain", "application/vnd.example+json".</summary>
    public static bool IsValidContentType(string? contentType) =>
        contentType is { Length: > 0 and <= MaxContentTypeLength } && ContentTypePattern().IsMatch(contentType);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex EndpointPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$")]
    private static partial Regex MetadataKeyPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9!#$&^_.+-]*/[a-z0-9][a-z0-9!#$&^_.+-]*(;.*)?$")]
    private static partial Regex ContentTypePattern();
}
