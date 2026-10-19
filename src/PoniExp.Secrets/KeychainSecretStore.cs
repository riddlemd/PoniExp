using System.Runtime.Versioning;
using PoniExp.Secrets.Interop;
using PoniExp.Secrets.Interop.MacOS;

namespace PoniExp.Secrets;

/// <summary>
/// macOS: the login keychain, through the Keychain interop ported from Git Credential Manager (via KHost).
/// Secrets are generic passwords under the service "PoniExp", one account per secret and home directory.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretStore(string homeDirectory) : ISecretStore
{
    private const string Service = "PoniExp";
    private readonly ICredentialStore _keychain = new MacOSKeychain();

    public string Description => "the login keychain (service \"PoniExp\")";

    public byte[]? Load(string name) =>
        _keychain.Get(Service, Account(name))?.Password is { } secret ? Convert.FromBase64String(secret) : null;

    public void Save(string name, byte[] secret) => _keychain.AddOrUpdate(Service, Account(name), Convert.ToBase64String(secret));

    public bool Delete(string name) => _keychain.Remove(Service, Account(name));

    // The home directory is part of the account, so separate homes never share keys.
    private string Account(string name) => $"{name}@{Path.GetFullPath(homeDirectory)}";
}

