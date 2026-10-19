using PoniExp.Core.Crypto;

namespace PoniExp.Secrets;

/// <summary>
/// Linux: each secret in its own file in the home directory, readable and writable by the owner only
/// (0600). Works headless, with no keyring service; disk encryption is what protects it at rest.
/// </summary>
public sealed class FileSecretStore(string homeDirectory) : ISecretStore
{
    public string Description => $"owner-only files in {homeDirectory}";

    public byte[]? Load(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Save(string name, byte[] secret)
    {
        Directory.CreateDirectory(homeDirectory);
        PrivateFiles.WriteAllBytes(PathOf(name), secret);
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

    private string PathOf(string name) => Path.Combine(homeDirectory, name + ".secret");
}
