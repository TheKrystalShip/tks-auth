using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Minting;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Anchor.Tests;

/// <summary>
/// Token revocation (RFC 7009) at <c>/revoke</c>: a client ends the session it holds with that session's
/// refresh token, and nobody learns anything about a token from the answer.
/// </summary>
/// <remarks>
/// Each test registers its own application, because the store and the journal are shared across the
/// collection. Sessions are minted in process as the mint site records them — a client, a provider session,
/// a refresh token audienced to the issuer — since what is under test is ending one, not getting one.
/// </remarks>
[Collection(AnchorCollection.Name)]
public sealed class TokenRevocationTests(AnchorFixture anchor)
{
    // ── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record Registered(string Id, string Client, string Origin, string? Secret);

    /// <summary>An application outside KGSM with one browser client, public or confidential.</summary>
    private async Task<Registered> ApplicationAsync(bool confidential = false)
    {
        string id = "rv" + Guid.NewGuid().ToString("N")[..8];
        string client = id + "-web";
        string origin = "https://" + id + ".test";
        ApplicationResult result = await anchor.Service<ApplicationRegistry>().AddAsync(
            new ApplicationRequest(id, "Revoke " + id, Audience: null, ManifestUrl: null, AccessLifetimeMinutes: null,
                DiscordApplications: null, ActForDiscord: null,
                [new ApplicationClientRequest(client, [origin + "/callback"], [], Confidential: confidential, Device: false)]),
            "local:test", origin: null, CancellationToken.None);
        Assert.Equal(ApplicationOutcome.Done, result.Outcome);
        return new Registered(id, client, origin, result.Secrets?.SingleOrDefault()?.Secret);
    }

    private sealed record Held(KgsmUser User, string Sid, string Refresh, string Access);

    /// <summary>A session of <paramref name="app"/>'s client, minted under a browser's sign-in.</summary>
    private async Task<Held> SessionOfAsync(Registered app)
    {
        (KgsmUser user, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "revoke");
        var tokens = anchor.Service<ISessionTokenService>();
        KgsmIdentity identity = user.AsIdentity();
        string sid = Endpoints.NewSessionId();
        MintedToken refresh = tokens.MintApplicationRefresh(identity, sid);
        await anchor.Service<SqliteSessionRegistry>().CreateAsync(
            new SessionRegistration(sid, identity.Handle, AnchorFixture.ClusterId, DateTimeOffset.UtcNow,
                refresh.ExpiresAt, "revoke-tests", refresh.Jti),
            browser.ProviderSession, app.Client);
        return new Held(user, sid, refresh.Token, browser.Access);
    }

    private async Task<HttpResponseMessage> RevokeAsync(
        string token, string? clientId, (string Id, string Secret)? basic = null, string? origin = null)
    {
        var form = new Dictionary<string, string> { ["token"] = token, ["token_type_hint"] = "refresh_token" };
        if (clientId is not null)
            form["client_id"] = clientId;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/revoke") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");
        if (basic is { } credentials)
        {
            string pair = Uri.EscapeDataString(credentials.Id) + ":" + Uri.EscapeDataString(credentials.Secret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return await anchor.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refresh, string? clientId, (string Id, string Secret)? basic = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh };
        if (clientId is not null)
            form["client_id"] = clientId;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/token") { Content = new FormUrlEncodedContent(form) };
        if (basic is { } credentials)
        {
            string pair = Uri.EscapeDataString(credentials.Id) + ":" + Uri.EscapeDataString(credentials.Secret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }
        return await anchor.Client.SendAsync(request);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .TryGetProperty("error", out JsonElement error) ? error.GetString() : null;

    private IReadOnlyList<JsonElement> RevokedLines(string sid) =>
        [.. anchor.Journal(AuthEvents.SessionRevoked)
            .Where(e => e.GetProperty("Data").GetProperty("Sid").GetString() == sid)];

    /// <summary>
    /// The answer an OIDC client library takes as success: <c>200</c> and no body, so no content type it
    /// could refuse to read.
    /// </summary>
    private static async Task AssertRevokedAnswer(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    // ── Revoking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_names_the_revocation_endpoint_and_how_a_client_authenticates_there()
    {
        using HttpClient client = anchor.Following();
        JsonElement discovery = JsonDocument.Parse(
            await client.GetStringAsync("/.well-known/openid-configuration")).RootElement;

        Assert.Equal(AnchorFixture.Issuer + "/revoke", discovery.GetProperty("revocation_endpoint").GetString());
        Assert.Equal(["none", "client_secret_basic", "client_secret_post"],
            discovery.GetProperty("revocation_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_public_client_ends_its_session_and_the_refresh_token_is_refused_after()
    {
        Registered app = await ApplicationAsync();
        Held held = await SessionOfAsync(app);

        await AssertRevokedAnswer(await RevokeAsync(held.Refresh, app.Client));

        Assert.False(await anchor.Service<SqliteSessionRegistry>().IsAliveAsync(held.Sid));
        HttpResponseMessage refused = await RefreshAsync(held.Refresh, app.Client);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_grant", await ErrorOf(refused));

        // Its holder ended it, and that is one line naming the session.
        JsonElement line = Assert.Single(RevokedLines(held.Sid));
        Assert.Equal(SessionRevokeScopes.Self, line.GetProperty("Data").GetProperty("Scope").GetString());
        Assert.Equal(held.User.UserId, line.GetProperty("Data").GetProperty("UserId").GetString());
        Assert.Equal(held.User.AsIdentity().ActorString, line.GetProperty("Actor").GetString());

        // Revoking it again answers the same and records nothing more.
        await AssertRevokedAnswer(await RevokeAsync(held.Refresh, app.Client));
        Assert.Single(RevokedLines(held.Sid));
    }

    [Fact]
    public async Task Another_client_cannot_revoke_a_session_it_was_not_issued()
    {
        Registered app = await ApplicationAsync();
        Registered other = await ApplicationAsync();
        Held held = await SessionOfAsync(app);

        HttpResponseMessage refused = await RevokeAsync(held.Refresh, other.Client);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("unauthorized_client", await ErrorOf(refused));

        Assert.True(await anchor.Service<SqliteSessionRegistry>().IsAliveAsync(held.Sid));
        Assert.Empty(RevokedLines(held.Sid));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(held.Refresh, app.Client)).StatusCode);
    }

    [Fact]
    public async Task A_confidential_clients_session_is_ended_only_with_its_secret()
    {
        Registered app = await ApplicationAsync(confidential: true);
        Held held = await SessionOfAsync(app);

        HttpResponseMessage anonymous = await RevokeAsync(held.Refresh, app.Client);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("invalid_client", await ErrorOf(anonymous));

        HttpResponseMessage wrong = await RevokeAsync(held.Refresh, clientId: null, basic: (app.Client, "not-the-secret"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Basic", Assert.Single(wrong.Headers.WwwAuthenticate).Scheme);
        Assert.True(await anchor.Service<SqliteSessionRegistry>().IsAliveAsync(held.Sid));

        await AssertRevokedAnswer(await RevokeAsync(held.Refresh, clientId: null, basic: (app.Client, app.Secret!)));
        Assert.False(await anchor.Service<SqliteSessionRegistry>().IsAliveAsync(held.Sid));
    }

    [Fact]
    public async Task A_token_that_ends_nothing_answers_as_one_that_did()
    {
        Registered app = await ApplicationAsync();
        Held held = await SessionOfAsync(app);

        await AssertRevokedAnswer(await RevokeAsync("not-a-token", app.Client));
        await AssertRevokedAnswer(await RevokeAsync("eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln", app.Client));

        // An access token is a bearer resource servers hold; presenting one ends nothing.
        await AssertRevokedAnswer(await RevokeAsync(held.Access, clientId: null));
        Assert.True(await anchor.Service<SqliteSessionRegistry>().IsAliveAsync(held.Sid));
    }

    [Fact]
    public async Task A_request_that_names_no_client_here_or_no_token_is_refused()
    {
        HttpResponseMessage unknown = await RevokeAsync("not-a-token", "no-such-client");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("invalid_client", await ErrorOf(unknown));

        HttpResponseMessage empty = await anchor.Client.PostAsync("/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token_type_hint"] = "refresh_token" }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("invalid_request", await ErrorOf(empty));
    }

    [Fact]
    public async Task A_clients_origin_reads_the_answer_across_origins()
    {
        Registered app = await ApplicationAsync();
        Held held = await SessionOfAsync(app);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/revoke");
        preflight.Headers.Add("Origin", app.Origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        HttpResponseMessage allowed = await anchor.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(app.Origin, allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));

        HttpResponseMessage revoked = await RevokeAsync(held.Refresh, app.Client, origin: app.Origin);
        await AssertRevokedAnswer(revoked);
        Assert.Equal(app.Origin, revoked.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
