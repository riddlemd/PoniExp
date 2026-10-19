using System.Text.Json;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Machines;

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options =
        WireProtocol.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    public static T? Read<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;

    /// <summary>Atomic replace, owner-only on Unix: these files say who may use the relay.</summary>
    public static void Write<T>(string path, T value) =>
        PrivateFiles.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}
