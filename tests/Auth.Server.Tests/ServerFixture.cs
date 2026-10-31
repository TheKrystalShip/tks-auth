using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

using TheKrystalShip.Auth.Minting;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>
/// One provider, standing on its own account store in a temporary directory.
/// </summary>
/// <remarks>
/// <para>
/// The daemon is started through its own <c>Program</c> rather than a stand-in, so what the tests
/// exercise is the composition that ships — the same key file, the same session registry, the same
/// live authority read on every request. A test that built its own service graph would prove the
/// graph the test wrote.
/// </para>
/// <para>
/// Configuration arrives through environment variables because that is how the daemon is configured
/// on a host: the settings file is the floor and a variable overrides one key of it. Setting them
/// before the host is built is what makes the paths land in a temporary directory instead of
/// <c>/var/lib</c>.
/// </para>
/// </remarks>
public sealed class ServerFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    /// <summary>Where this fixture's files live. Removed with the fixture.</summary>
    public string Root { get; }

    /// <summary>The account store, open for a test to seed directly through the library.</summary>
    public SqliteAuthorityStore Store { get; }

    /// <summary>The password hasher the daemon itself uses, so a seeded password is a real one.</summary>
    public IUserPasswordHasher Hasher { get; } = new IdentityPasswordHasher();

    /// <summary>Where this provider says it is reached, as a deployment behind a proxy has to.</summary>
    public const string SignInUrl = "https://auth.test";

    /// <summary>
    /// The issuer: the URL this provider signs people in at, which every token names and the OpenID Connect
    /// doors are served under.
    /// </summary>
    public const string Issuer = SignInUrl;

    /// <summary>A site on another origin, registered as a client, so its origin is one the provider admits.</summary>
    public const string PanelUrl = "https://panel.test";

    // The daemon is configured through process environment variables, read as its host is built. Fixtures
    // are built one at a time, so a second one never reads the first's.
    private static readonly Lock BuildGate = new();

    public ServerFixture()
    {
        lock (BuildGate)
        {
        Root = Path.Combine(Path.GetTempPath(), "tks-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Environment.SetEnvironmentVariable("Auth__UserStorePath", Path.Combine(Root, "users.db"));
        Environment.SetEnvironmentVariable("Auth__SessionStorePath", Path.Combine(Root, "sessions.db"));
        Environment.SetEnvironmentVariable("Auth__SigningKeyPath", Path.Combine(Root, "signing.pem"));
        Environment.SetEnvironmentVariable("Auth__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Auth__AllowSelfRegistration", "true");

        // The provider's pages, pointed at a folder that holds nothing unless a test installs them. Left
        // at its default, a run on a host with tks-auth installed tests those pages instead of the floor,
        // and one without it tests the floor — the same suite measuring the host.
        Environment.SetEnvironmentVariable("Auth__UiPath", Path.Combine(Root, "ui"));

        // An identity provider's OAuth application, as /etc/tks-auth/providers.env supplies it on a real
        // machine. Present so the provider door is wired at all — nothing here reaches a provider,
        // because every case under test is decided before an exchange is attempted.
        Environment.SetEnvironmentVariable("Auth__Providers__discord__ClientId", "test-client-id");
        Environment.SetEnvironmentVariable("Auth__Providers__discord__ClientSecret", "test-client-secret");

        Store = new SqliteAuthorityStore(new UserStoreOptions { Path = Path.Combine(Root, "users.db") });

        // Discord, answered in process. The provider door's tests are decided before Discord would be
        // reached; a token exchange asks it who a token belongs to, and a test says what it answers.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddProvider(_audit));
                builder.ConfigureTestServices(services => services
                    .AddHttpClient(nameof(DiscordDirectory))
                    .ConfigurePrimaryHttpMessageHandler(() => Discord));
            });
        Client = _factory.CreateClient();

        // A site on another origin, registered the way any application is, so its origin is a client's.
        ApplicationResult registered = Service<ApplicationRegistry>().AddAsync(
            new ApplicationRequest("panel", "Panel", Audience: null, ManifestUrl: null,
                AccessLifetimeMinutes: null, DiscordApplications: null, ActForDiscord: null,
                [new ApplicationClientRequest(
                    "panel.test", [PanelUrl + "/signed-in"], [PanelUrl + "/"], Confidential: false)]),
            "local:test", origin: null, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(ApplicationOutcome.Done, registered.Outcome);
        }
    }

    private readonly AuditCapture _audit = new();

    /// <summary>A client onto the running provider.</summary>
    public HttpClient Client { get; }

    /// <summary>What the running provider reaches when it calls <c>discord.com</c>.</summary>
    public FakeDiscord Discord { get; } = new();

    /// <summary>Something out of the running daemon's own service graph.</summary>
    public T Service<T>() => (T)_factory.Services.GetService(typeof(T))!;

    /// <summary>
    /// A client that reports a redirect rather than following it — the whole of what an OAuth bounce
    /// and the handoff back to a panel are, so following one would assert against wherever it landed
    /// instead of against what this provider said.
    /// </summary>
    public HttpClient Following() => _factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// An account with a password, as somebody would have created it: an Owner, or holding nothing but
    /// <c>everyone</c>.
    /// </summary>
    public async Task<UserAccount> SeedAsync(
        string username, string password, bool owner = false, UserStatus status = UserStatus.Active)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var user = new UserAccount(
            UserIds.NewUserId(), username, username, AccountOrigin.Admitted, status, now, now);

        await Store.CreateAsync(user);
        await Store.AddCredentialAsync(new UserCredential(
            UserIds.NewCredentialId(), user.UserId, CredentialKind.Password,
            UserCredentials.LocalHandle(user.UserId), Hasher.Hash(password),
            Label: null, Created: now, LastUsed: null));

        if (owner)
            await Store.GrantOwnerLocallyAsync(username, "local:test", now);

        return user;
    }

    /// <summary>
    /// A public client registered once under <paramref name="clientId"/>, as the only client of an
    /// application of the same name, so a sign-in through it ends in an application's token.
    /// </summary>
    public async Task ClientAsync(string clientId, string name, string[] redirects, string[] logouts)
    {
        if (Service<ClientRegistry>().Find(clientId) is not null)
            return;

        ApplicationResult result = await Service<ApplicationRegistry>().AddAsync(
            new ApplicationRequest("t-" + clientId, name, Audience: null, ManifestUrl: null,
                AccessLifetimeMinutes: null, DiscordApplications: null, ActForDiscord: null,
                [new ApplicationClientRequest(clientId, redirects, logouts, Confidential: false)]),
            "local:test", origin: null, CancellationToken.None);
        Assert.Equal(ApplicationOutcome.Done, result.Outcome);
    }

    /// <summary>
    /// A session as a surface holds one — its bearer, its refresh token, and the ids behind them — and
    /// the provider cookie the browser that signed in holds.
    /// </summary>
    public sealed record Session(string Access, string Refresh, string Sid, string ProviderSession, string Cookie)
    {
        /// <summary>A request from that browser to one of the provider's own pages.</summary>
        public HttpRequestMessage AtTheAccountPage(HttpMethod method, string path, object? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", $"{ProviderCookies.Session}={Cookie}");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("Accept", "application/json");
            if (body is not null)
            {
                request.Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            }
            return request;
        }
    }

    /// <summary>
    /// Sign <paramref name="user"/> in, the way a browser completing the provider's round trip would end
    /// up: a provider session, and a session minted under it.
    /// </summary>
    /// <remarks>
    /// In process rather than through the pages, because a test about administering accounts or reading
    /// the roster is not a test about signing in — the OpenID Connect suite drives that end to end. Built
    /// from the running daemon's own token service and registry, so what is presented is what the provider
    /// verifies, and ending the provider session ends this one exactly as it would a browser's.
    /// </remarks>
    public async Task<Session> SignInAsync(UserAccount user, string device = "provider-tests", AccountIdentity? arrivedAs = null)
    {
        var registry = Service<SqliteSessionRegistry>();
        var tokens = Service<TheKrystalShip.Auth.Minting.ISessionTokenService>();
        AccountIdentity identity = arrivedAs ?? user.AsIdentity();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string provider = Endpoints.NewSessionId();
        string cookie = ProviderCookies.NewSecret();
        await registry.CreateProviderSessionAsync(
            provider, identity.Handle, StoredIdentity.From(identity).ToJson(),
            ProviderCookies.Hash(cookie), Issuer, now, now.AddDays(1), device);

        string sid = Endpoints.NewSessionId();
        var access = tokens.MintApplicationAccess(new ApplicationAccess(
            user.UserId, user.Username, user.DisplayName, Picture: null, sid, Application.ProviderClientId,
            Application.ProviderAudience, TimeSpan.FromMinutes(15), Actions: []));
        var refresh = tokens.MintApplicationRefresh(identity, sid);
        await registry.CreateAsync(
            new SessionRegistration(
                sid, identity.Handle, Issuer, now, refresh.ExpiresAt, device, refresh.Jti),
            provider);

        return new Session(access.Token, refresh.Token, sid, provider, cookie);
    }

    /// <summary>A fresh account, an Owner or not, signed in.</summary>
    public async Task<(UserAccount User, Session Session)> SignedInAsync(bool owner = false, string prefix = "user")
    {
        UserAccount user = await SeedAsync(
            prefix + "-" + Guid.NewGuid().ToString("N")[..10], "a long enough password", owner);
        return (user, await SignInAsync(user));
    }

    /// <summary>
    /// Every line this provider has recorded, in the order it recorded them.
    /// </summary>
    /// <remarks>
    /// Read off the log lines the audit writes, as the envelope <c>EventType</c>, <c>Actor</c>,
    /// <c>Origin</c> and <c>Data</c>: the payload exactly as it was written.
    /// </remarks>
    public IReadOnlyList<JsonElement> Journal() => _audit.Lines();

    /// <summary>Every line of one type, most recent last.</summary>
    public IReadOnlyList<JsonElement> Journal(string eventType) =>
        [.. Journal().Where(e => e.GetProperty("EventType").GetString() == eventType)];

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file the daemon still holds open. The temp tree is disposable either way.
        }
    }
}

/// <summary>
/// Discord's <c>GET /oauth2/@me</c>, as a test sets it up: each access token answers with the
/// application it was issued to and the user it belongs to, or with a status.
/// </summary>
/// <remarks>
/// It counts every question per token, which is how a test tells an answer held by the provider from
/// one it asked for again. A token nobody set up is one Discord does not honour.
/// </remarks>
public sealed class FakeDiscord : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _asked = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="token"/> belongs to <paramref name="userId"/> and was issued to
    /// <paramref name="application"/>, living until <paramref name="expires"/> (an hour when absent).
    /// </summary>
    public void Issue(string token, string application, string userId, string username, DateTimeOffset? expires = null)
    {
        string json = JsonSerializer.Serialize(new
        {
            application = new { id = application, name = "Activity " + application },
            scopes = new[] { "identify" },
            expires = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToString("O"),
            user = new { id = userId, username, global_name = username.ToUpperInvariant(), avatar = (string?)null },
        });
        Answer(token, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });
    }

    /// <summary><paramref name="token"/> is answered with <paramref name="status"/> and no body worth reading.</summary>
    public void Fail(string token, System.Net.HttpStatusCode status) =>
        Answer(token, () => new HttpResponseMessage(status) { Content = new StringContent("{}") });

    /// <summary>How many times Discord has been asked about <paramref name="token"/>.</summary>
    public int Asked(string token)
    {
        lock (_answers)
            return _asked.GetValueOrDefault(token);
    }

    private void Answer(string token, Func<HttpResponseMessage> answer)
    {
        lock (_answers)
            _answers[token] = answer;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string token = request.Headers.Authorization?.Parameter ?? "";
        Func<HttpResponseMessage>? answer;
        lock (_answers)
        {
            _asked[token] = _asked.GetValueOrDefault(token) + 1;
            _answers.TryGetValue(token, out answer);
        }

        if (request.RequestUri?.AbsolutePath != "/api/oauth2/@me" || answer is null)
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        return Task.FromResult(answer());
    }

    // The client factory disposes the handler chain it built; this one outlives every chain it is put in.
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// One provider per run.
/// </summary>
/// <remarks>
/// Shared rather than per-test because the daemon is configured through process environment
/// variables, and two fixtures standing at once would each set them for the other.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>
{
    public const string Name = "provider";
}

/// <summary>Collects the audit lines the provider logs.</summary>
internal sealed class AuditCapture : ILoggerProvider
{
    private const string Category = "TheKrystalShip.Auth.Server.AuditLog";

    private readonly List<string> _lines = [];

    public ILogger CreateLogger(string categoryName) =>
        categoryName == Category ? new Capture(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    /// <summary>Every audit line so far, in the envelope shape the tests read.</summary>
    public IReadOnlyList<JsonElement> Lines()
    {
        string[] snapshot;
        lock (_lines)
            snapshot = [.. _lines];

        var result = new List<JsonElement>();
        foreach (string line in snapshot)
        {
            // audit <event> actor=<actor> origin=<origin> <payload json>
            string[] head = line.Split(' ', 5);
            using JsonDocument payload = JsonDocument.Parse(head[4]);
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("EventType", head[1]);
                writer.WriteString("Actor", head[2]["actor=".Length..]);
                writer.WriteString("Origin", head[3]["origin=".Length..]);
                writer.WritePropertyName("Data");
                payload.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }

            result.Add(JsonDocument.Parse(stream.ToArray()).RootElement.Clone());
        }

        return result;
    }

    private sealed class Capture(AuditCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (!message.StartsWith("audit ", StringComparison.Ordinal))
                return;

            lock (owner._lines)
                owner._lines.Add(message);
        }
    }
}

/// <summary>
/// A provider of its own for the tests that replace the catalog and the Owners, which would otherwise
/// change what every other test finds.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AuthorityCollection : ICollectionFixture<ServerFixture>
{
    public const string Name = "authority";
}
