using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// The validated form of <see cref="ServerSettings"/>: what the daemon actually runs on, with every
/// value clamped to something it can honour.
/// </summary>
/// <remarks>
/// Separate from the settings type so the raw configuration and the runtime view stay distinguishable.
/// A value below its floor is raised rather than refused — a daemon that will not start because a
/// number is one below a bound is worse than one that starts and says what it used.
/// </remarks>
/// <param name="ListenAddress">Where Kestrel binds.</param>
/// <param name="Issuer">The <c>iss</c> claim and the provider's one address, when it is a URL.</param>
/// <param name="UserStorePath">The account store.</param>
/// <param name="SessionStorePath">Where live sessions are recorded.</param>
/// <param name="SigningKeyPath">The private signing key.</param>
/// <param name="AccessLifetime">How long an access bearer lives.</param>
/// <param name="RefreshLifetime">The absolute session cap.</param>
/// <param name="SessionCleanup">How often expired session rows are swept.</param>
/// <param name="Pending">What is allowed to accumulate while nobody has approved it.</param>
/// <param name="AllowSelfRegistration">Whether somebody with no account may make one.</param>
/// <param name="ReauthWindow">How long a proved credential lets somebody change what proves them.</param>
/// <param name="UiPath">Where the provider's pages are installed.</param>
/// <param name="ManifestRefresh">How often every application's action manifest is read again.</param>
/// <param name="DiscordTokenCache">The longest Discord's answer about one of its tokens is held for an exchange.</param>
internal sealed record ServerOptions(
    string ListenAddress,
    string Issuer,
    string UserStorePath,
    string SessionStorePath,
    string SigningKeyPath,
    TimeSpan AccessLifetime,
    TimeSpan RefreshLifetime,
    TimeSpan SessionCleanup,
    PendingPolicy Pending,
    bool AllowSelfRegistration,
    TimeSpan ReauthWindow,
    string UiPath,
    TimeSpan ManifestRefresh,
    TimeSpan DiscordTokenCache)
{
    /// <summary>
    /// Where the bootstrap Owner account's one-time password is left, on a provider whose account store
    /// was empty when it first started.
    /// </summary>
    /// <remarks>
    /// Beside the session store, in the directory only this daemon writes: a credential belongs to the
    /// daemon that minted it, wherever the account store has been configured to live.
    /// </remarks>
    public string InitialAdminPasswordPath =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(SessionStorePath)) is { Length: > 0 } dir ? dir : ".",
            "initial-admin-password");

    /// <summary>
    /// The issuer as the browser-facing URL the OpenID Connect doors are served under, or null when the
    /// configured issuer is not one.
    /// </summary>
    /// <remarks>
    /// Configuration, never inferred from a request: a Host header is the caller's to set, and an issuer
    /// taken from one would let any caller choose what every token it is handed says about who minted
    /// it. A provider with no URL here serves no OpenID Connect door, and says so rather than guessing.
    /// </remarks>
    public Uri? IssuerUrl =>
        Uri.TryCreate(Issuer, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? uri
            : null;

    /// <summary>The issuer's origin — what a browser sends as <c>Origin</c> from the provider's own pages.</summary>
    public string? IssuerOrigin => IssuerUrl?.GetLeftPart(UriPartial.Authority);

    public static ServerOptions FromSettings(ServerSettings s)
    {
        return new ServerOptions(
            ListenAddress: Text(s.ListenAddress, "http://0.0.0.0:8098"),
            // A URL loses the trailing slash a person naturally types, so the discovery document, every
            // token's iss and the gossiped fact all state the one string a client compares them by.
            Issuer: (s.Issuer?.Trim() ?? "").TrimEnd('/'),
            UserStorePath: Text(s.UserStorePath, "/var/lib/tks-auth/accounts.db"),
            SessionStorePath: Text(s.SessionStorePath, "/var/lib/tks-auth/sessions.db"),
            SigningKeyPath: Text(s.SigningKeyPath, "/var/lib/tks-auth/session-signing.pem"),
            AccessLifetime: TimeSpan.FromMinutes(
                AtLeast(s.AccessLifetimeMinutes ?? 15, ServerSettings.Floors.AccessLifetimeMinutes)),
            RefreshLifetime: TimeSpan.FromDays(
                AtLeast(s.RefreshLifetimeDays ?? 30, ServerSettings.Floors.RefreshLifetimeDays)),
            SessionCleanup: TimeSpan.FromMinutes(
                AtLeast(s.SessionCleanupMinutes ?? 60, ServerSettings.Floors.SessionCleanupMinutes)),
            // Off unless the deployment says otherwise. It is an unauthenticated write, and a provider that
            // has not decided to take strangers should not be taking them because a default did.
            AllowSelfRegistration: s.AllowSelfRegistration ?? false,
            Pending: new PendingPolicy(
                Cap: Math.Max(0, s.PendingCap ?? 25),
                Ttl: TimeSpan.FromDays(AtLeast(s.PendingTtlDays ?? 14, 1))),
            ReauthWindow: TimeSpan.FromMinutes(AtLeast(s.ReauthWindowMinutes ?? 5, 1)),
            UiPath: Text(s.UiPath, "/opt/tks-auth/ui"),
            ManifestRefresh: TimeSpan.FromMinutes(
                AtLeast(s.ManifestRefreshMinutes ?? 15, ServerSettings.Floors.ManifestRefreshMinutes)),
            DiscordTokenCache: TimeSpan.FromMinutes(AtLeast(s.DiscordTokenCacheMinutes ?? 10, 0)));
    }

    private static string Text(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static int AtLeast(int value, int floor) => value < floor ? floor : value;
}
