using System.Text.Json;
using System.Text.Json.Serialization;
using PoniExp.Core.Crypto;

namespace PoniExp.Server.Machines;

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;

    /// <summary>Atomic replace, owner-only on Unix: these files say who may use the relay.</summary>
    public static void Write<T>(string path, T value) =>
        PrivateFiles.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}
