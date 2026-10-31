namespace TheKrystalShip.Auth.Server;

/// <summary>
/// The provider's configurable surface, shaped 1:1 with the <c>"Auth"</c> section of
/// <c>tks-auth.settings.json</c>. That file is the floor: every knob is declared there with
/// its default, and an environment variable may only override a key that exists in it
/// (<c>Auth__ListenAddress</c>, <c>Auth__Issuer</c>). A variable naming a key this class does
/// not declare sets nothing.
/// </summary>
/// <remarks>
/// This type holds what was <em>written</em>, not what the daemon runs on. <see cref="ServerOptions"/>
/// is the validated form, so the raw configuration and the runtime view stay separable.
/// <para>
/// Every number is <b>nullable</b>, and null means "not written" — the coded default in
/// <see cref="ServerOptions"/> applies. Two binder behaviours make that load-bearing: a blank value
/// (<c>Auth__AccessLifetimeMinutes=</c>, one stray line in an env file) binds to a non-nullable
/// <see cref="int"/> by throwing, taking the daemon down at startup; and a JSON null binds to
/// <c>0</c>, silently discarding a property initializer's default. Nullable turns both into "unset",
/// while a value that is present and is not a number still fails loudly.
/// </para>
/// </remarks>
internal sealed class ServerSettings
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Auth";

    /// <summary>
    /// The lowest value each lifetime may take. Declared once and read by both
    /// <see cref="ServerOptions.FromSettings"/>, which raises anything lower.
    /// </summary>
    public static class Floors
    {
        /// <summary>A one-minute access token is already short enough to be a refresh loop.</summary>
        public const int AccessLifetimeMinutes = 1;

        /// <summary>A session shorter than a day is a sign-in prompt, not a session.</summary>
        public const int RefreshLifetimeDays = 1;

        /// <summary>A sweep faster than a minute is a busy loop over rows nothing is reading.</summary>
        public const int SessionCleanupMinutes = 1;

        /// <summary>Reading every application's manifest more often than this is a request per minute for nothing new.</summary>
        public const int ManifestRefreshMinutes = 1;
    }

    /// <summary>How often every application's action manifest is read again.</summary>
    public int? ManifestRefreshMinutes { get; set; }

    /// <summary>
    /// The longest Discord's answer about one of its access tokens is held for a token exchange. Zero asks
    /// Discord on every exchange.
    /// </summary>
    public int? DiscordTokenCacheMinutes { get; set; }

    /// <summary>Whether somebody with no account may make one.</summary>
    public bool? AllowSelfRegistration { get; set; }

    /// <summary>How many accounts may be awaiting approval at once.</summary>
    public int? PendingCap { get; set; }

    /// <summary>How long an unapproved account survives before it is removed.</summary>
    public int? PendingTtlDays { get; set; }

    /// <summary>How long a proved credential lets somebody keep changing what proves their account.</summary>
    public int? ReauthWindowMinutes { get; set; }

    /// <summary>Where Kestrel listens. TCP, because a browser signs in here directly.</summary>
    public string ListenAddress { get; set; } = "http://0.0.0.0:8098";

    /// <summary>
    /// The <c>iss</c> claim and this provider's one address: the origin browsers sign in at and every
    /// provider callback is built on. Set by the deployment, because it is what the service is:
    /// changing it signs everybody out and moves every registered callback. The OpenID Connect doors are served only while it is an absolute
    /// URL.
    /// </summary>
    public string Issuer { get; set; } = "";

    /// <summary>The account store.</summary>
    public string UserStorePath { get; set; } = "/var/lib/tks-auth/accounts.db";

    /// <summary>Where live sessions are recorded.</summary>
    public string SessionStorePath { get; set; } = "/var/lib/tks-auth/sessions.db";

    /// <summary>The private key sessions are signed with.</summary>
    public string SigningKeyPath { get; set; } = "/var/lib/tks-auth/session-signing.pem";

    /// <summary>
    /// Where the provider's pages are installed: the bundle built from this repo's <c>web/</c>, beside the
    /// binary, served at <c>/ui/</c>.
    /// </summary>
    public string UiPath { get; set; } = "/opt/tks-auth/ui";

    /// <summary>Access-token lifetime in minutes. Raised to <see cref="Floors.AccessLifetimeMinutes"/> if lower.</summary>
    public int? AccessLifetimeMinutes { get; set; }

    /// <summary>The absolute session cap in days. Raised to <see cref="Floors.RefreshLifetimeDays"/> if lower.</summary>
    public int? RefreshLifetimeDays { get; set; }

    /// <summary>Sweep cadence for expired session rows. Raised to <see cref="Floors.SessionCleanupMinutes"/> if lower.</summary>
    public int? SessionCleanupMinutes { get; set; }
}
