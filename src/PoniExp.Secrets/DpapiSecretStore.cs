using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PoniExp.Secrets;

/// <summary>
/// Windows: each secret in its own file in the home directory, encrypted with DPAPI for the current user.
/// Only the same Windows account on the same machine can decrypt it, so a copied file is useless.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string homeDirectory) : ISecretStore
{
    public string Description => $"DPAPI-protected files in {homeDirectory} (this Windows user only)";

    public byte[]? Load(string name)
    {
        var path = PathOf(name);
        return File.Exists(path)
            ? ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(name), DataProtectionScope.CurrentUser)
            : null;
    }

    public void Save(string name, byte[] secret)
    {
        Directory.CreateDirectory(homeDirectory);
        Core.Crypto.PrivateFiles.WriteAllBytes(PathOf(name), ProtectedData.Protect(secret, Entropy(name), DataProtectionScope.CurrentUser));
    }

    public bool Delete(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string PathOf(string name) => Path.Combine(homeDirectory, name + ".dpapi");

    // Ties each blob to its name, so one secret's file can't be passed off as another's.
    private static byte[] Entropy(string name) => Encoding.UTF8.GetBytes("poniexp/" + name);
}
