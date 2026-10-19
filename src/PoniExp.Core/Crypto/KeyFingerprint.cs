using System.Security.Cryptography;

namespace PoniExp.Core.Crypto;

public static class KeyFingerprint
{
    /// <summary>The machine id: the first 128 bits of SHA-256 over the public key, as 32 lowercase hex characters.</summary>
    public static string MachineIdOf(string publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey)))[..32];

    /// <summary>
    /// What the owner compares on the joining machine's screen before approving it: the first 96 bits of
    /// the machine id in six groups of four. A key that grinds to the same code is not feasible in the time
    /// a request waits for approval.
    /// </summary>
    public static string JoinCodeOf(string publicKey)
    {
        var id = MachineIdOf(publicKey);
        return string.Join('-', Enumerable.Range(0, 6).Select(i => id.Substring(i * 4, 4)));
    }
}
