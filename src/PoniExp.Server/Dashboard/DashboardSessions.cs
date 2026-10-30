using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;
using PoniExp.Server.Owner;

namespace PoniExp.Server.Dashboard;

/// <summary>A signed-in dashboard: the owner key's fingerprint it was signed with, and when it ends.</summary>
public sealed record DashboardSession(string Fingerprint, DateTimeOffset ExpiresOn);

/// <summary>
/// Dashboard sign-in (docs/plan.md, Phase 13): an owner-signed <see cref="DashboardSignIn"/> becomes a short-lived JWT (HS256)
/// in the browser's cookie. Nothing is stored: the HMAC key lives in memory only, so a router restart signs everyone out,
/// and a token stops working when the owner key changes.
/// </summary>
public sealed class DashboardSessions(OwnerDesk desk, RouterKeys keys, IOptionsMonitor<RouterOptions> options, TimeProvider time,
    ILogger<DashboardSessions> logger)
{
    public const string Cookie = "poniexp-dashboard";

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly DateTimeOffset _started = time.GetUtcNow();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _used = new();

    /// <summary>Checks a sign-in and returns the token for it.</summary>
    /// <exception cref="RouterException">It isn't the owner's, for this router, now, and new.</exception>
    public (string Token, DashboardSession Session) SignIn(DashboardSignIn signIn)
    {
        var owner = desk.Owner ?? throw new RouterException("The router has no owner key yet; set it with docker exec (poniexp admin owner set).");
        var now = time.GetUtcNow();
        var skew = options.CurrentValue.ClockSkew;
        if (signIn.RouterId != keys.Id)
        {
            throw new RouterException("That sign-in is for another router.");
        }

        if (!signIn.IsSignedBy(owner))
        {
            throw new RouterException("That sign-in isn't signed by the owner's key.");
        }

        if ((now - signIn.IssuedOn).Duration() > skew || signIn.IssuedOn < _started)
        {
            throw new RouterException("That sign-in link is too old (or this machine's clock is off); run poniexp-client dashboard again.");
        }

        foreach (var old in _used.Where(u => now - u.Value > skew * 2))
        {
            _used.TryRemove(old.Key, out _);
        }

        if (!_used.TryAdd(signIn.Id, now))
        {
            throw new RouterException("That sign-in link was already used; run poniexp-client dashboard again.");
        }

        var session = new DashboardSession(owner.Fingerprint, now + options.CurrentValue.DashboardSessionLifetime);
        logger.LogInformation("Dashboard signed in with the owner key {Fingerprint}, until {Until:u}", owner.Fingerprint, session.ExpiresOn);
        return (Issue(session), session);
    }

    /// <summary>The session a token stands for, or null: tampered, expired, or signed with an owner key that has since changed.</summary>
    public DashboardSession? Validate(string? token)
    {
        if (token?.Split('.') is not [var header, var body, var signature] || header != Header)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(header + "." + body))))
        {
            return null;
        }

        Claims? claims;
        try
        {
            claims = JsonSerializer.Deserialize<Claims>(Base64Url.DecodeFromChars(body));
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }

        if (claims is null || DateTimeOffset.FromUnixTimeSeconds(claims.Exp) <= time.GetUtcNow() || desk.Owner?.Fingerprint != claims.Sub)
        {
            return null;
        }

        return new DashboardSession(claims.Sub, DateTimeOffset.FromUnixTimeSeconds(claims.Exp));
    }

    private static readonly string Header = Base64Url.EncodeToString("""{"alg":"HS256","typ":"JWT"}"""u8);

    private string Issue(DashboardSession session)
    {
        var claims = new Claims(session.Fingerprint, time.GetUtcNow().ToUnixTimeSeconds(), session.ExpiresOn.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"));
        var body = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims));
        return $"{Header}.{body}.{Sign(Header + "." + body)}";
    }

    private string Sign(string data) => Base64Url.EncodeToString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(data)));

    private sealed record Claims(
        [property: System.Text.Json.Serialization.JsonPropertyName("sub")] string Sub,
        [property: System.Text.Json.Serialization.JsonPropertyName("iat")] long Iat,
        [property: System.Text.Json.Serialization.JsonPropertyName("exp")] long Exp,
        [property: System.Text.Json.Serialization.JsonPropertyName("jti")] string Jti);
}
