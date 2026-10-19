namespace PoniExp.Secrets;

/// <summary>
/// Where a machine's private keys are kept, protected by the OS where it can. One store per PoniExp home
/// directory, so several homes (tests, a second profile) never share keys.
/// </summary>
public interface ISecretStore
{
    /// <summary>Where the secrets live, for messages ("DPAPI files in ~/.poniexp", "the login keychain").</summary>
    string Description { get; }

    /// <summary>The secret filed under <paramref name="name"/>, or null when there is none.</summary>
    byte[]? Load(string name);

    /// <summary>Stores a secret, replacing any already filed under the same name.</summary>
    void Save(string name, byte[] secret);

    /// <summary>Forgets a secret. False when there was nothing to forget.</summary>
    bool Delete(string name);
}
