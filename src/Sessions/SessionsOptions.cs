namespace TheKrystalShip.Auth.Sessions;

/// <summary>
/// The provider a service admits people from, and how the service answers them. The service signs nobody
/// in: it reads the provider's discovery and keys, checks each access token it is handed, and takes the
/// person's actions from one claim.
/// </summary>
public sealed class SessionsOptions
{
    /// <summary>The configuration section the options bind from unless the service names another.</summary>
    public const string Section = "Sessions";

    /// <summary>The provider's issuer, exactly as its tokens carry it in <c>iss</c>. Discovery is read from under it.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>The audience the service's access tokens are minted for. A token for any other is refused.</summary>
    public string Audience { get; set; } = "";

    /// <summary>The claim listing the actions a person holds, an array of action ids.</summary>
    public string ActionsClaim { get; set; } = "tks_actions";

    /// <summary>
    /// The action ids the service declares in its manifest, or null for a service that checks actions it
    /// does not declare itself. When set, the host refuses to start while a route names any other, and a
    /// person holds only these.
    /// </summary>
    public IReadOnlySet<string>? DeclaredActions { get; set; }

    /// <summary>
    /// Writes the body of a refusal, in the service's own error shape. The status and, for
    /// <see cref="Refusal.SignIn"/>, the <c>WWW-Authenticate: Bearer</c> challenge are already set. Unset,
    /// the body is <c>{ "error": { "code", "message" } }</c>.
    /// </summary>
    public Func<HttpContext, Refusal, Task>? WriteRefusal { get; set; }

    /// <summary>Runs once a request's token has been checked and the person read from it.</summary>
    public Action<HttpContext, SessionPerson>? Admitted { get; set; }

    /// <summary>Where discovery is read from: the issuer without a trailing slash.</summary>
    public string Authority => Issuer.TrimEnd('/');
}

/// <summary>Why a request is refused.</summary>
public enum Refusal
{
    /// <summary>A 401: the request carries no usable token.</summary>
    SignIn,

    /// <summary>A 403: the person may not do this.</summary>
    NotAllowed,
}
