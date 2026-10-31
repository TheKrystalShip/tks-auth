using System.Security.Cryptography;
using System.Text.RegularExpressions;

using System.Net;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// The clients this provider issues codes to, and the only places it sends a browser back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration is the consent.</b> There is no consent screen: a client is here because somebody
/// registered it, which is a decision about the whole provider rather than a question for the person
/// signing in.
/// </para>
/// <para>
/// <b>Every URI is matched exactly.</b> A prefix or a pattern is a redirect this provider did not decide
/// to make, and an authorization endpoint that redirects anywhere a pattern admits is an open redirect
/// carrying a code.
/// </para>
/// <para>
/// Held in memory, because it is read on every request that carries an <c>Origin</c> and on every
/// authorization. It is reloaded after every write this process makes, and when the store's generation
/// says another process — the host command — has written since.
/// </para>
/// <para>
/// <b>Every client belongs to an application,</b> and a client with none signs nobody in. An application's
/// clients are the ones registered for it (<see cref="ApplicationRegistry"/>). tks-auth's own admin pages,
/// on the issuer's origin, are declared instead of stored: that client is what this build serves, so it
/// comes back with every start and wins over a stored client of the same id.
/// </para>
/// </remarks>
internal sealed partial class ClientRegistry
{
    /// <summary>How long a read trusts the snapshot before asking the store whether it has moved.</summary>
    private static readonly TimeSpan GenerationCheck = TimeSpan.FromSeconds(1);

    private readonly SqliteSessionRegistry _store;
    private readonly RegisteredClient? _declared;
    private readonly Lock _reload = new();
    private volatile Snapshot _snapshot;
    private long _checkedAt;

    private sealed record Snapshot(
        IReadOnlyDictionary<string, RegisteredClient> ById,
        IReadOnlySet<string> Origins,
        IReadOnlyDictionary<string, Application> Applications,
        long Generation);

    /// <param name="store">The session store the registered clients and applications live in.</param>
    /// <param name="issuer">
    /// This provider's issuer; when it is a URL, the admin pages' client is declared on its origin.
    /// </param>
    public ClientRegistry(SqliteSessionRegistry store, string? issuer = null)
    {
        _store = store;
        _declared = AdminPages(issuer, DateTimeOffset.UtcNow);
        _snapshot = Read();
        _checkedAt = Environment.TickCount64;
    }

    /// <summary>
    /// The admin pages' client: public, built in, and sending its codes and sign-outs back to the pages on
    /// the issuer's own origin. Null when the issuer is not a URL, where no OpenID Connect door is served.
    /// </summary>
    /// <remarks>
    /// Declared, never stored: it is what this build serves, so it comes back on every start, and nothing
    /// can register over it or remove it.
    /// </remarks>
    private static RegisteredClient? AdminPages(string? issuer, DateTimeOffset now)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http"))
            return null;

        string origin = uri.GetLeftPart(UriPartial.Authority);
        string pages = origin + Application.ProviderPagesPath;
        // A signed-out browser is sent to the account page, which asks it to sign in: the admin pages
        // answer only a live administrator's session, so returning there would land on an unknown path.
        return new RegisteredClient(
            Application.ProviderClientId, "tks-auth", [pages], [pages, origin + "/account"], ClientSources.Builtin,
            now, Application.ProviderId);
    }

    /// <summary>The snapshot, reloaded first when another process has written the store since it was read.</summary>
    private Snapshot Current
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _checkedAt) < (long)GenerationCheck.TotalMilliseconds)
                return _snapshot;

            lock (_reload)
            {
                if (now - _checkedAt >= (long)GenerationCheck.TotalMilliseconds)
                {
                    if (_store.RegistryGeneration() != _snapshot.Generation)
                        _snapshot = Read();
                    Interlocked.Exchange(ref _checkedAt, now);
                }

                return _snapshot;
            }
        }
    }

    /// <summary>Every registered client, of every application.</summary>
    public IReadOnlyList<RegisteredClient> All => [.. Current.ById.Values.OrderBy(c => c.ClientId, StringComparer.Ordinal)];

    /// <summary>The clients of one application.</summary>
    public IReadOnlyList<RegisteredClient> Of(string applicationId) =>
        [.. All.Where(c => string.Equals(c.ApplicationId, applicationId, StringComparison.Ordinal))];

    /// <summary>The client with this id, or null.</summary>
    public RegisteredClient? Find(string? clientId) =>
        clientId is { Length: > 0 } && Current.ById.TryGetValue(clientId, out RegisteredClient? client) ? client : null;

    /// <summary>Every application registered here.</summary>
    public IReadOnlyList<Application> Applications =>
        [.. Current.Applications.Values.OrderBy(a => a.Id, StringComparer.Ordinal)];

    /// <summary>The registered application with this id, or null.</summary>
    public Application? FindApplication(string? applicationId) =>
        applicationId is { Length: > 0 } && Current.Applications.TryGetValue(applicationId, out Application? a) ? a : null;

    /// <summary>
    /// Whether <paramref name="origin"/> is where some registered client lives, of any application. Such
    /// an origin may read what this provider publishes and exchange codes, and nothing that reads its
    /// cookie.
    /// </summary>
    public bool IsClientOrigin(string origin) => Current.Origins.Contains(origin);

    /// <summary>Every origin one of an application's clients lives at.</summary>
    public IReadOnlyCollection<string> OriginsOf(string applicationId) =>
        [.. Of(applicationId).SelectMany(c => c.RedirectUris.Concat(c.PostLogoutRedirectUris))
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Select(OriginOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Whether <paramref name="uri"/> is one of the client's redirect URIs, exactly.</summary>
    public static bool Redirects(RegisteredClient client, string? uri) =>
        uri is { Length: > 0 } && client.RedirectUris.Contains(uri, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="uri"/> is one of the client's post-sign-out URIs, exactly.</summary>
    public static bool ReturnsAfterSignOut(RegisteredClient client, string? uri) =>
        uri is { Length: > 0 } && client.PostLogoutRedirectUris.Contains(uri, StringComparer.Ordinal);

    /// <summary>The origin of a URI a client is registered with.</summary>
    public static string OriginOf(string uri) => new Uri(uri).GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// A client registered by hand, or why the request cannot be one.
    /// </summary>
    /// <remarks>
    /// A public client needs somewhere to send a code, unless it signs in with the device grant: a device
    /// is answered at <c>/token</c> and never takes a browser anywhere. A confidential one may have nowhere
    /// either: a service that authenticates with its secret and exchanges a token never takes a browser
    /// through a sign-in. A device client is public, because a device in somebody's living room keeps no
    /// secret.
    /// </remarks>
    internal static (RegisteredClient? Client, string? Problem) Shape(
        string? clientId, string name, IReadOnlyList<string>? redirectUris, IReadOnlyList<string>? postLogoutUris,
        string? applicationId, string? secretHash, DateTimeOffset now, bool device = false)
    {
        string id = string.IsNullOrWhiteSpace(clientId)
            ? "cli_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))
            : clientId.Trim();
        if (!ClientIdShape().IsMatch(id))
            return (null,
                "A client id is 3–64 lowercase letters, digits, '.', '_' or '-', beginning with a letter or a digit.");

        if (device && secretHash is not null)
            return (null, "A device client is public: a device keeps no secret.");

        IReadOnlyList<string> redirects = redirectUris ?? [];
        if (redirects.Count == 0 && secretHash is null && !device)
            return (null, "A public client needs at least one redirect URI, unless it signs in with the device grant.");

        foreach (string uri in redirects.Concat(postLogoutUris ?? []))
        {
            if (Problem(uri) is { } problem)
                return (null, $"'{uri}': {problem}");
        }

        return (new RegisteredClient(
            id, name, [.. redirects.Distinct(StringComparer.Ordinal)],
            [.. (postLogoutUris ?? []).Distinct(StringComparer.Ordinal)],
            ClientSources.Admin, now, applicationId, secretHash, device), null);
    }

    /// <summary>
    /// Whether a client id is in use. A declared client is not stored, so the store would accept its id
    /// and the declaration would then hide the row it wrote.
    /// </summary>
    internal bool IsTaken(string clientId) => Find(clientId) is not null;

    /// <summary>
    /// Why <paramref name="uri"/> cannot be registered, or null when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HTTPS, or plain HTTP on this machine, a private network or a local name. A site on a LAN is served
    /// over plain HTTP, and refusing it here would leave the machine with nowhere a code can be sent. On
    /// such a network the operator owns the wire, and a code seen on it is still worthless without the
    /// verifier the browser holds. Anywhere else a code in the clear crosses somebody's path.
    /// </para>
    /// <para>
    /// No fragment, which a code redirect cannot carry, and no user information, which is how a URL is
    /// dressed as another host.
    /// </para>
    /// </remarks>
    internal static string? Problem(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
            return "not an absolute URI";
        if (!string.IsNullOrEmpty(parsed.Fragment) || uri.Contains('#'))
            return "a redirect cannot carry a fragment";
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return "a redirect cannot carry user information";
        if (parsed.Scheme is not ("https" or "http"))
            return "only https, or http on this machine or a private network";
        return parsed.Scheme == "https" || IsPrivateHost(parsed.Host)
            ? null
            : "only https, or http on this machine or a private network";
    }

    /// <summary>Whether a host is this machine, a private address or a local name.</summary>
    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address))
            return !host.Contains('.');

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
            return true;

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        byte[] b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Read the store again, after a write this process made.</summary>
    internal Task ReloadAsync(CancellationToken ct)
    {
        lock (_reload)
        {
            _snapshot = Read();
            Interlocked.Exchange(ref _checkedAt, Environment.TickCount64);
        }

        return Task.CompletedTask;
    }

    private Snapshot Read()
    {
        // The generation first: a write landing between it and the lists is picked up by the next check
        // rather than hidden under a generation that already counts it.
        long generation = _store.RegistryGeneration();
        IReadOnlyList<RegisteredClient> stored = _store.ListClientsAsync().GetAwaiter().GetResult();
        IReadOnlyList<Application> applications = _store.ListApplicationsAsync().GetAwaiter().GetResult();
        return Load(stored, applications, generation);
    }

    private Snapshot Load(IReadOnlyList<RegisteredClient> stored, IReadOnlyList<Application> applications, long generation)
    {
        var apps = applications.ToDictionary(a => a.Id, StringComparer.Ordinal);

        // An application's client is shown by its application's name, which a rename changes in one place.
        var byId = stored
            .Select(c => c.ApplicationId is { } app && apps.TryGetValue(app, out Application? owner) ? c with { Name = owner.Name } : c)
            .ToDictionary(c => c.ClientId, StringComparer.Ordinal);
        if (_declared is { } declared)
            byId[declared.ClientId] = declared;

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegisteredClient client in byId.Values)
        {
            foreach (string uri in client.RedirectUris.Concat(client.PostLogoutRedirectUris))
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out _))
                    origins.Add(OriginOf(uri));
            }
        }

        return new Snapshot(byId, origins, apps, generation);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,63}$")]
    private static partial Regex ClientIdShape();
}
