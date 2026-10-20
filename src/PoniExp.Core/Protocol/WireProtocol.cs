using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoniExp.Core.Protocol;

public static class WireProtocol
{
    /// <summary>
    /// Checked when a connection says hello. Both sides must match exactly: the message format is not
    /// negotiated, so an older or newer client gets a clear "upgrade" refusal instead of garbled messages.
    /// </summary>
    /// 4: Hello returns the router's time and clock-skew tolerance; receipt and agent message bodies.
    /// 5: attachments (encrypted blobs) and file receipts.
    /// 6: groups, rooms (a signed membership log on the router, fanned-out room envelopes); endpoints are agents.
    /// 7: the job board (signed job actions, open jobs on the router); no roles.
    /// 8: approvals (approval-request/-answer/-withdrawn), the owner key and the desk (here).
    /// 9: owner admin from anywhere (the router's keys in Hello, RunAdmin, owner handovers, signed groups, joins on the desk).
    /// 10: signed-only changes (docker exec through the desk), revocations, encryption key rotation (keys signed with their issue time).
    public const int Version = 10;

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
