using TheKrystalShip.Auth.Minting;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>Minting a session's two tokens for an identity, the way the provider mints them for its own admin pages.</summary>
internal static class TestMint
{
    public static MintedToken MintAccess(this ISessionTokenService tokens, AccountIdentity identity, string sessionId) =>
        tokens.MintApplicationAccess(new ApplicationAccess(
            identity.Handle, identity.Username, identity.Display, identity.AvatarUrl, sessionId,
            Application.ProviderClientId, Application.ProviderAudience, TimeSpan.FromMinutes(15), Actions: []));

    public static MintedToken MintRefresh(this ISessionTokenService tokens, AccountIdentity identity, string sessionId) =>
        tokens.MintApplicationRefresh(identity, sessionId);
}
