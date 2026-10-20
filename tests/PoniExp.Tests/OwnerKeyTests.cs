using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

/// <summary>Phase 9: the owner's SSH key and its signatures.</summary>
public class OwnerKeyTests
{
    // Made by OpenSSH with a throwaway key: ssh-keygen -t ed25519; printf "hello desk" > m; ssh-keygen -Y sign -f k -n poniexp m
    private const string OpenSshPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFeGIg6Cc72Fe4YEhAqhFaIcL4iEb0SrN+QU/9vpopdX probe owner";

    private const string OpenSshSignature = """
        -----BEGIN SSH SIGNATURE-----
        U1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAgV4YiDoJzvYV7hgSECqEVohwviI
        RvRKs35BT/2+mil1cAAAAHcG9uaWV4cAAAAAAAAAAGc2hhNTEyAAAAUwAAAAtzc2gtZWQy
        NTUxOQAAAEDCYcR23fccL5CfRegac1lLVELnKPscIFF/W1/kYSLOdkTfM3aqiS/AwLUbdy
        Zz+oGZyhd+ocCRJcZOH01mXvoB
        -----END SSH SIGNATURE-----
        """;

    [Fact]
    public void ASignatureMadeByOpenSshVerifies()
    {
        var owner = OwnerKey.Parse(OpenSshPublicKey);
        Assert.Equal("probe owner", owner.Comment);
        Assert.StartsWith("SHA256:", owner.Fingerprint);
        var signature = owner.FromArmored(OpenSshSignature);
        Assert.NotNull(signature);
        Assert.True(owner.Verify("hello desk"u8.ToArray(), signature));
        Assert.False(owner.Verify("hello desk!"u8.ToArray(), signature));
        Assert.Null(TestOwner.Create().Key.FromArmored(OpenSshSignature)); // made by another key
    }

    [Fact]
    public void AHereClaimNeedsTheOwnersSignatureAndAtMostSevenDays()
    {
        var owner = TestOwner.Create();
        var now = DateTimeOffset.UtcNow;
        var claim = owner.Here("m1", now, now.AddHours(12));
        Assert.Null(claim.Problem(owner.Key));
        Assert.Equal("m1", claim.DeskAt(now));
        Assert.Null(claim.DeskAt(now.AddHours(13)));
        Assert.Equal("not signed by the owner's key", claim.Problem(TestOwner.Create().Key));
        Assert.Equal("not signed by the owner's key", (claim with { MachineId = "m2" }).Problem(owner.Key));
        Assert.Equal("a desk lasts at most 7 days", owner.Here("m1", now, now.AddDays(8)).Problem(owner.Key));
        Assert.Throws<FormatException>(() => OwnerKey.Parse("ssh-rsa AAAAB3NzaC1yc2E= someone"));
    }
}
