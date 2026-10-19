using System.Text;

namespace PoniExp.Core.Crypto;

/// <summary>
/// The exact bytes a signature covers. Every field is length-prefixed, so no field can bleed into the next
/// (a name ending in "\n" can't impersonate another field), and the purpose comes first, so a signature
/// made for one purpose (say, a join request) is never valid for another (an envelope).
/// </summary>
public static class SigningPayload
{
    public const string Version = "poniexp/1";

    public static byte[] Build(string purpose, params IEnumerable<string?> fields)
    {
        var builder = new StringBuilder(Version).Append('\n');
        Append(builder, purpose);
        foreach (var field in fields)
        {
            Append(builder, field ?? string.Empty);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value).Append('\n');
}
