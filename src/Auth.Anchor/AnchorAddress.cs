namespace TheKrystalShip.Auth.Anchor;

/// <summary>
/// Where a browser reaches this provider, and the provider callbacks built from it.
/// </summary>
/// <remarks>
/// <para>
/// The issuer's origin, and only that. A sign-in begins on the issuer's origin and its request in flight
/// is a cookie there, so a provider's round trip that returned to any other name would arrive without it
/// and complete nothing. One address means a page reached by another name cannot start one.
/// </para>
/// <para>
/// A provider accepts only redirect URIs registered on its application, so the callback has to be one of
/// them or the bounce is refused at the provider, where no log here sees it. Building both callbacks from
/// one base means the two a provider needs registered can never name different origins.
/// </para>
/// </remarks>
internal sealed class AnchorAddress(AnchorOptions options)
{
    /// <summary>The origin a browser reaches this provider at, or null while the issuer is not a URL.</summary>
    public string? Base => options.IssuerOrigin;

    /// <summary>Where a provider sends the browser back after a sign-in, or null with no address.</summary>
    public string? RedirectUri(string provider) =>
        Base is { } origin ? $"{origin}/auth/{provider}/callback" : null;

    /// <summary>
    /// Where a provider sends the browser back when somebody is <em>attaching</em> an account rather
    /// than signing in with one, or null with no address.
    /// </summary>
    /// <remarks>
    /// A separate address because the two arrivals mean different things and must not be confused: one
    /// mints a session for whoever comes back, the other attaches whoever comes back to an account
    /// that is already signed in.
    /// </remarks>
    public string? LinkRedirectUri(string provider) =>
        Base is { } origin ? $"{origin}/auth/identities/{provider}/callback" : null;
}
