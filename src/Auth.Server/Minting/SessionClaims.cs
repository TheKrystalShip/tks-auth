using System.Security.Claims;

namespace TheKrystalShip.Auth.Minting;

/// <summary>
/// Reads identity and session back out of a validated token's claims.
/// </summary>
/// <remarks>
/// The profile here is the snapshot taken at login, not a live read: the provider keeps no external
/// identity provider's token, so there is nothing to re-fetch with.
/// </remarks>
public static class SessionClaims
{
    /// <summary>
    /// The identity a token carries, or <see langword="null"/> when the subject is absent or is not a
    /// <c>provider:subject</c> handle. A caller with no readable identity is treated as unauthenticated
    /// rather than as an anonymous someone.
    /// </summary>
    /// <remarks>
    /// The provider is read from the handle rather than matched against a list this build knows: nothing
    /// about access rides on a token's identity, so reading an unfamiliar provider grants nothing.
    /// </remarks>
    public static AccountIdentity? ReadIdentity(ClaimsIdentity ci)
    {
        string? sub = ci.FindFirst("sub")?.Value ?? ci.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!ActorStrings.TryParse(sub, out string provider, out string subject))
            return null;

        string username = ci.FindFirst(AuthClaims.Username)?.Value ?? subject;
        string display = ci.FindFirst(AuthClaims.Display)?.Value ?? username;
        string? avatar = ci.FindFirst(AuthClaims.Avatar)?.Value;
        string scope = ci.FindFirst("scope")?.Value ?? "";

        return new AccountIdentity(
            provider, subject, username, display, avatar,
            [.. scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
    }

    /// <summary>
    /// The session id, or <see langword="null"/> on a token that carries none. A token with no session is
    /// one no registry can revoke, so a surface that has a registry rejects it.
    /// </summary>
    public static string? ReadSessionId(ClaimsIdentity ci) =>
        ci.FindFirst(AuthClaims.SessionId)?.Value;

    /// <summary>
    /// The per-token id. On the refresh path it is checked against what the session currently holds, which
    /// is the reuse detection.
    /// </summary>
    public static string? ReadJti(ClaimsIdentity ci) =>
        ci.FindFirst(AuthClaims.Jti)?.Value;
}
