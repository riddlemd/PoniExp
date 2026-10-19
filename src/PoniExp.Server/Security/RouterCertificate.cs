using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PoniExp.Core.Crypto;

namespace PoniExp.Server.Security;

/// <summary>
/// The router's TLS certificate: self-signed, created on first start in the data directory, kept across
/// restarts. Clients don't use a CA; they pin <see cref="RouterCertificateInfo.Sha256"/>, so host names
/// and expiry don't matter to them.
/// </summary>
public static class RouterCertificate
{
    public static X509Certificate2 LoadOrCreate(string dataDirectory, TimeProvider time)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "router.pfx");
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=poniexp-server", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddDnsName("poniexp-server");
            request.CertificateExtensions.Add(names.Build());
            var now = time.GetUtcNow();
            using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
            PrivateFiles.WriteAllBytes(path, created.Export(X509ContentType.Pkcs12));
        }

        // SChannel can't serve TLS from an ephemeral key; elsewhere, don't leave the key in a store.
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, flags);
    }

    public static string Sha256Of(X509Certificate2 certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
}

/// <summary>What clients pin. Null when the relay runs without TLS (tests, or behind a TLS proxy).</summary>
public sealed record RouterCertificateInfo(string? Sha256);
