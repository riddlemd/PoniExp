using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoniExp.Core.Protocol;

public static class WireProtocol
{
    /// <summary>
    /// Checked when a connection says hello. Both sides must match exactly: the message format is not
    /// negotiated, so an older or newer client gets a clear "upgrade" refusal instead of garbled messages.
    /// </summary>
    public const int Version = 3;

    /// <summary>Enums travel as kebab-case names ("file-receipt"), on the wire and in files.</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    public static readonly JsonSerializerOptions Json = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>The kebab-case name of an enum value, as it is signed and stored.</summary>
    public static string NameOf<T>(T value) where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
