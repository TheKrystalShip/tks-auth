using Microsoft.Extensions.Caching.Memory;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Minting;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// The provider itself: accounts, sessions, the OpenID Connect provider, the applications it signs people
/// in to and their clients, the account and admin doors, and the audit log.
/// </summary>
internal static class TksAuthCore
{
    /// <param name="services">The daemon's services.</param>
    /// <param name="options">Its validated settings.</param>
    /// <param name="signer">The session signing key, loaded before the host is built.</param>
    internal static IServiceCollection AddTksAuthCore(
        this IServiceCollection services, ServerOptions options, EcdsaSessionSigner signer)
    {
        services.AddSingleton(options);
        services.AddSingleton(signer);

        services.AddSingleton<IUserStore>(sp => sp.GetRequiredService<ServerAuthority>().Store
            ?? throw new InvalidOperationException(sp.GetRequiredService<ServerAuthority>().UnavailableReason));
        services.AddSingleton<IUserPasswordHasher, IdentityPasswordHasher>();

        // How long a switched-off account can still be found usable. Short, because the read behind it is
        // a local point query and there is nothing to buy by keeping it long.
        services.AddSingleton(sp => new AccountResolver(
            sp.GetRequiredService<IUserStore>(), TimeSpan.FromSeconds(5)));

        services.AddSingleton(sp => new LocalSignInService(
            sp.GetRequiredService<IUserStore>(),
            sp.GetRequiredService<IUserPasswordHasher>()));

        // The audit of what happened to the accounts: one structured log line per fact.
        services.AddSingleton<AuditLog>();
        services.AddSingleton<AdminRefusals>();

        // The Owner account an empty store gets. Without it a fresh install is a door nobody can open —
        // registration is off unless configured on, and an account made through it waits for an
        // approval only somebody holding auth:accounts.approve can give.
        services.AddHostedService<ServerBootstrapper>();

        // The links the account page has started and a provider has not yet sent back. In memory: a
        // restart drops links in flight, which costs a click and cannot grant anything.
        services.AddSingleton<LinkTicketStore>();

        // The session token service signs with the private key, so a verifier can check a token and
        // cannot mint one. The audience of each token is its application's.
        services.AddSingleton<ISessionTokenService>(_ => new SessionTokenService(
            new SessionTokenOptions(
                BearerAudience: Application.ProviderAudience,
                AccessLifetime: options.AccessLifetime,
                RefreshLifetime: options.RefreshLifetime,
                Issuer: options.Issuer),
            signer));

        // Who may do what, and the one place a catalog report lands and is journaled.
        services.AddSingleton<ServerAuthority>();
        services.AddSingleton<AuthorityIntake>();
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton(_ => new SqliteSessionRegistry(options.SessionStorePath));
        services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SqliteSessionRegistry>());
        services.AddSingleton<AdminAccess>();

        // The OpenID Connect provider: the clients it answers, the browsers signed in at it, and the
        // id_token beside every session it mints. Its rows live beside the sessions, because a browser's
        // sign-in here is a session and every session minted through it records which one it came from.
        services.AddSingleton(sp => new ClientRegistry(
            sp.GetRequiredService<SqliteSessionRegistry>(), options.Issuer));

        // The applications those clients sign people in to — each registered by
        // the admin surface or the host command — and the actions each declares in the manifest it serves,
        // read on registration and on an interval.
        AddApplications(services);
        services.AddHostedService<ApplicationManifestWorker>();

        // The provider's own actions enter its catalog on every start, from the binary.
        services.AddHostedService<OwnActionsCatalog>();
        services.AddSingleton<ProviderSessions>();
        services.AddSingleton<IdTokens>();
        services.AddSingleton<ProviderBundle>();
        services.AddSingleton<ReauthRoundTrips>();
        services.AddSingleton<DeviceCodeGuesses>();
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton<ISessionValidator>(sp => new SessionValidator(
            sp.GetRequiredService<ISessionRegistry>(),
            sp.GetRequiredService<IMemoryCache>(),
            TimeSpan.FromSeconds(5)));
        services.AddSingleton<SessionReader>();
        services.AddSingleton<ServerAuth>();
        services.AddSingleton<AuthorityCaller>();

        // Signing in with an account somebody already holds elsewhere. The provider answers WHO, and the
        // account store answers what they may do — so a provider is added with no authority story of its
        // own. Transient like the typed HttpClient underneath it: holding one for the process lifetime
        // pins its handler and silently stops the factory rotating it, so DNS changes never land.
        services.AddHttpClient(nameof(DiscordDirectory), c => c.Timeout = TimeSpan.FromSeconds(10));

        // Who a Discord token an application exchanges belongs to, remembered for its life up to a cap,
        // on the same typed client.
        services.AddSingleton<DiscordAuthorizations>();
        services.AddSingleton(sp => new ServerAddress(sp.GetRequiredService<ServerOptions>()));
        services.AddTransient(sp => new ProviderCatalog(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ServerAddress>()));
        services.AddSingleton(sp => new IdentityLinkService(sp.GetRequiredService<IUserStore>()));

        // Deletes rows already past their cap. Housekeeping — it ends no session that is still alive.
        services.AddHostedService(sp => new SessionCleanupWorker(
            sp.GetRequiredService<ISessionRegistry>(),
            options.SessionCleanup,
            sp.GetRequiredService<ILogger<SessionCleanupWorker>>()));

        return services;
    }

    /// <summary>
    /// The application registry and the catalog of what applications declare: the one path every change
    /// to an application takes, from the admin surface and from the host command alike.
    /// </summary>
    internal static IServiceCollection AddApplications(IServiceCollection services)
    {
        services.AddHttpClient(ApplicationCatalog.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<ApplicationCatalog>();
        services.AddSingleton<ApplicationRegistry>();
        return services;
    }

    /// <summary>Every door the provider serves.</summary>
    internal static WebApplication MapTksAuthCore(this WebApplication app)
    {
        // Liveness probe: 200 means the provider is up and serving.
        app.MapGet("/health", () => Results.Text("ok\n"));

        // The OpenID Connect provider every browser surface signs in through. Served only when the issuer
        // is the provider's browser-facing URL; otherwise each door says so rather than guessing one.
        app.MapGet("/.well-known/openid-configuration", OidcEndpoints.Discovery);
        app.MapGet("/.well-known/jwks.json", OidcEndpoints.Jwks);
        app.MapGet("/authorize", OidcEndpoints.Authorize);
        app.MapPost("/authorize/credentials", OidcEndpoints.Credentials);
        app.MapGet("/authorize/wait", OidcEndpoints.Wait);
        app.MapGet("/authorize/floor.css", ProviderPages.StylesheetAsync);
        app.MapGet("/authorize/context", OidcEndpoints.Context);
        app.MapPost("/authorize/register", OidcEndpoints.Register);
        app.MapGet("/authorize/{provider}", OidcEndpoints.ProviderStart);
        app.MapPost("/token", OidcEndpoints.Token);
        app.MapPost("/revoke", TokenRevocation.Revoke);
        app.MapGet("/userinfo", OidcEndpoints.UserInfo);
        app.MapPost("/userinfo", OidcEndpoints.UserInfo);
        app.MapGet("/sign-out", OidcEndpoints.SignOut);
        app.MapPost("/sign-out", OidcEndpoints.ConfirmSignOut);

        // The device grant: a device asks for its codes here and collects its session at /token, and a
        // person signed in on another screen approves it at /device.
        app.MapPost("/device_authorization", DeviceAuthorization.Begin);
        app.MapGet(DeviceAuthorization.VerificationPath, DevicePageEndpoints.Page);
        app.MapGet("/device/context", DevicePageEndpoints.Context);
        app.MapGet("/device/confirm", DevicePageEndpoints.Confirm);
        app.MapPost("/device/decision", DevicePageEndpoints.Decide);

        // The provider's own pages, and the account page's calls. Every one of those is authenticated by
        // the provider's cookie and answered on this origin alone; the origin itself opens the account
        // page, which is where an app on this origin starts. The admin pages are a client of this
        // provider and call the admin routes below with the bearer they hold.
        app.MapGet("/ui/{**path}", ProviderBundle.ServeAssetAsync);
        app.MapGet(Application.ProviderPagesPath, ProviderBundle.ServeAdminAsync);
        app.MapGet("/", (HttpContext ctx) => ctx.Response.Redirect("/account"));
        app.MapGet("/account", AccountPageEndpoints.Page);
        app.MapGet("/account/sign-in", AccountPageEndpoints.SignIn);
        app.MapGet("/account/me", AccountPageEndpoints.Me);
        app.MapPost("/account/reauth", AccountPageEndpoints.Reauth);
        app.MapGet("/account/reauth/{provider}", AccountPageEndpoints.ReauthWithProvider);
        app.MapPost("/account/password", AccountPageEndpoints.SetPassword);
        app.MapPost("/account/identities/{provider}/start", AccountPageEndpoints.StartLink);
        app.MapDelete("/account/identities/{credentialId}", AccountPageEndpoints.Unlink);
        app.MapPost("/account/sessions/revoke", AccountPageEndpoints.Revoke);
        app.MapPost("/account/sessions/name", AccountPageEndpoints.Rename);
        app.MapPost("/account/sign-out", AccountPageEndpoints.SignOut);

        // The applications this provider signs people in to, their clients and their client secrets.
        ApplicationEndpoints.Map(app);

        // Where a provider sends the browser back: to complete the request in flight, or to prove the
        // person again for the account page. The one address registered with the provider's application.
        app.MapGet("/auth/{provider}/callback", ProviderEndpoints.Callback);

        // Each gated route declares its action on itself (`AuthAction`); the handler enforces that entry
        // and `GET /auth/admin/operations` publishes it. Approving and switching off share a route,
        // told apart by the status asked for.
        app.MapGet("/auth/admin/users", Endpoints.Accounts);
        app.MapPost("/auth/admin/users", AccountEndpoints.CreateAccount)
            .WithMetadata(new AuthAction(AuthActions.AccountsCreate));
        app.MapPatch("/auth/admin/users/{userId}", Endpoints.PatchAccount)
            .WithMetadata(new AuthAction(AuthActions.AccountsApprove, "status", "active"))
            .WithMetadata(new AuthAction(AuthActions.AccountsDisable, "status", "disabled"));

        // Where a provider sends the browser back after the account page began attaching an identity. A
        // different address from the sign-in callback: one attaches whoever comes back to an account
        // already signed in, and sharing an address would let a link return through the sign-in door.
        app.MapGet("/auth/identities/{provider}/callback", IdentityEndpoints.CompleteLink);

        // What may be done to somebody else's account, each on its own action. Setting a password knows
        // no current one, because the case it exists for is a person who has lost theirs.
        app.MapPost("/auth/admin/users/{userId}/password", AccountEndpoints.SetPassword)
            .WithMetadata(new AuthAction(AuthActions.AccountsCreate));
        app.MapDelete("/auth/admin/users/{userId}", AccountEndpoints.DeleteAccount)
            .WithMetadata(new AuthAction(AuthActions.AccountsDelete));

        // The devices somebody is signed in on, and ending them.
        app.MapGet("/auth/admin/users/{userId}/sessions", SessionEndpoints.List)
            .WithMetadata(new AuthAction(AuthActions.AccountsDisable));
        app.MapPost("/auth/admin/users/{userId}/sessions/revoke-all", SessionEndpoints.RevokeAll)
            .WithMetadata(new AuthAction(AuthActions.AccountsDisable));
        app.MapPost("/auth/admin/users/{userId}/sessions/{sid}/revoke", SessionEndpoints.RevokeOne)
            .WithMetadata(new AuthAction(AuthActions.AccountsDisable));

        // Who may do what: the authority for the pages that administer it, one change at a time, whether
        // the rules would allow a change without making it, and the caller's own actions. 503 while the
        // store cannot be read.
        app.MapGet("/auth/admin/authority", AuthorityEndpoints.Read);
        app.MapPost(AdminOperations.EditsRoute, AuthorityEndpoints.Edit);
        app.MapPost(AdminOperations.ChecksRoute, AuthorityEndpoints.Check);
        app.MapGet("/me/access", AuthorityEndpoints.MeAccess);

        // Every gated route here and the action it requires, built from the routes themselves. Public: it
        // is a description of this build, and a client reads it to know which action a request it is
        // about to make needs, without holding a list of its own.
        app.MapGet("/auth/admin/operations", AdminOperations.Serve);

        return app;
    }
}
