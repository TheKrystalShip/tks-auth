using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Minting;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Anchor.Tests;

/// <summary>
/// The device authorization grant (RFC 8628), driven as a television and a phone drive it: the device asks
/// for its codes and polls <c>/token</c>, and a person signed in here approves it at <c>/device</c>.
/// </summary>
/// <remarks>
/// Each test registers its own application with one device client, because the store and the journal are
/// shared across the collection.
/// </remarks>
[Collection(AnchorCollection.Name)]
public sealed class DeviceGrantTests(AnchorFixture anchor)
{
    private const string Television = "SmartTV/1.0 (Android TV)";

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record Registered(string Id, string Name, string Client);

    /// <summary>An application outside KGSM with one device client, or with a browser client instead.</summary>
    private async Task<Registered> ApplicationAsync(bool device = true)
    {
        string id = "dv" + Guid.NewGuid().ToString("N")[..8];
        string client = id + "-tv";
        ApplicationResult result = await anchor.Service<ApplicationRegistry>().AddAsync(
            new ApplicationRequest(id, "Cinema " + id, Audience: null, ManifestUrl: null, AccessLifetimeMinutes: null,
                DiscordApplications: null, ActForDiscord: null,
                [new ApplicationClientRequest(client,
                    device ? [] : ["https://" + id + ".test/callback"], [], Confidential: false, Device: device)]),
            "local:test", origin: null, CancellationToken.None);
        Assert.Equal(ApplicationOutcome.Done, result.Outcome);
        return new Registered(id, "Cinema " + id, client);
    }

    private sealed record Codes(string DeviceCode, string UserCode, string VerificationUri, string Complete, long ExpiresIn, int Interval);

    private async Task<HttpResponseMessage> BeginRawAsync(string client, string? name = null)
    {
        using HttpClient device = anchor.Following();
        var form = new Dictionary<string, string> { ["client_id"] = client, ["scope"] = "openid" };
        if (name is not null)
            form["device_name"] = name;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/device_authorization")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.UserAgent.ParseAdd(Television);
        return await device.SendAsync(request);
    }

    private async Task<Codes> BeginAsync(string client, string? name = null)
    {
        HttpResponseMessage response = await BeginRawAsync(client, name);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        JsonElement body = await Json(response);
        return new Codes(
            body.GetProperty("device_code").GetString()!,
            body.GetProperty("user_code").GetString()!,
            body.GetProperty("verification_uri").GetString()!,
            body.GetProperty("verification_uri_complete").GetString()!,
            body.GetProperty("expires_in").GetInt64(),
            body.GetProperty("interval").GetInt32());
    }

    private async Task<HttpResponseMessage> PollAsync(string client, string deviceCode)
    {
        using HttpClient device = anchor.Following();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = DeviceAuthorization.GrantType,
                ["device_code"] = deviceCode,
                ["client_id"] = client,
            }),
        };
        request.Headers.UserAgent.ParseAdd(Television);
        return await device.SendAsync(request);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        (await Json(response)).TryGetProperty("error", out JsonElement error) ? error.GetString() : null;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static JsonElement Claims(string jwt)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> DecideAsync(AnchorFixture.Session session, string userCode, bool approve, string? name = null)
    {
        using HttpClient browser = anchor.Following();
        using HttpRequestMessage request = name is null
            ? session.AtTheAccountPage(HttpMethod.Post, "/device/decision", new { userCode, approve })
            : session.AtTheAccountPage(HttpMethod.Post, "/device/decision", new { userCode, approve, name });
        return await browser.SendAsync(request);
    }

    private async Task<HttpResponseMessage> ContextAsync(AnchorFixture.Session session, string userCode)
    {
        using HttpClient browser = anchor.Following();
        using HttpRequestMessage request = session.AtTheAccountPage(HttpMethod.Get,
            "/device/context?user_code=" + Uri.EscapeDataString(userCode));
        return await browser.SendAsync(request);
    }

    // ── The grant ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_names_the_device_endpoint_and_the_grant()
    {
        using HttpClient client = anchor.Following();
        JsonElement discovery = await Json(await client.GetAsync("/.well-known/openid-configuration"));

        Assert.Equal(AnchorFixture.Issuer + "/device_authorization",
            discovery.GetProperty("device_authorization_endpoint").GetString());
        Assert.Contains(DeviceAuthorization.GrantType,
            discovery.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_device_approved_from_a_browser_gets_a_session_of_its_own()
    {
        Registered app = await ApplicationAsync();
        (KgsmUser user, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");

        Codes codes = await BeginAsync(app.Client);
        Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$", codes.UserCode);
        Assert.Equal(AnchorFixture.Issuer + "/device", codes.VerificationUri);
        Assert.Equal(codes.VerificationUri + "?user_code=" + codes.UserCode, codes.Complete);
        Assert.Equal(600, codes.ExpiresIn);
        Assert.Equal(DeviceAuthorization.IntervalSeconds, codes.Interval);

        // Waiting, and told to slow down when it asks again at once.
        Assert.Equal("authorization_pending", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
        Assert.Equal("slow_down", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));

        // The page shows the person what they are approving, read from the code in any spelling.
        HttpResponseMessage context = await ContextAsync(browser, codes.UserCode.Replace("-", "").ToLowerInvariant());
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        JsonElement shown = (await Json(context)).GetProperty("device");
        Assert.Equal(app.Name, shown.GetProperty("application").GetString());
        Assert.Equal(Television, shown.GetProperty("userAgent").GetString());
        Assert.Equal(codes.UserCode, shown.GetProperty("userCode").GetString());

        HttpResponseMessage decided = await DecideAsync(browser, codes.UserCode, approve: true);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal("approved", (await Json(decided)).GetProperty("decision").GetString());

        HttpResponseMessage collected = await PollAsync(app.Client, codes.DeviceCode);
        Assert.Equal(HttpStatusCode.OK, collected.StatusCode);
        JsonElement tokens = await Json(collected);
        JsonElement access = Claims(tokens.GetProperty("access_token").GetString()!);
        Assert.Equal(app.Id, access.GetProperty("aud").GetString());
        Assert.Equal(user.UserId, access.GetProperty("sub").GetString());
        Assert.Equal(app.Client, access.GetProperty("client_id").GetString());
        Assert.False(tokens.TryGetProperty("id_token", out _));
        string sid = access.GetProperty("sid").GetString()!;

        // The code is spent.
        Assert.Equal("invalid_grant", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));

        // It is listed as a device's, recorded as a sign-in, and outlives the browser that approved it.
        var registry = anchor.Service<SqliteSessionRegistry>();
        IReadOnlyList<SqliteSessionRegistry.LiveSession> live = await registry.ListAsync([user.AsIdentity().Handle]);
        Assert.Equal(SqliteSessionRegistry.DeviceKind, live.Single(s => s.SessionId == sid).Kind);
        Assert.Equal(Television, live.Single(s => s.SessionId == sid).UserAgent);
        Assert.Contains(anchor.Journal(AuthEvents.SignedIn),
            e => e.GetProperty("Data").GetProperty("Sid").GetString() == sid);

        await registry.EndProviderSessionAsync(browser.ProviderSession);
        Assert.True(await registry.IsAliveAsync(sid));

        // And it renews as the public client it was minted for.
        using HttpClient device = anchor.Following();
        HttpResponseMessage renewed = await device.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = app.Client,
        }));
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }

    [Fact]
    public async Task A_device_is_named_by_what_it_sends_and_by_the_person_who_approves_it()
    {
        Registered app = await ApplicationAsync();
        (KgsmUser user, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");

        // The name the TV's owner gave it, as the device sends it, arrives cleaned for the page.
        Codes codes = await BeginAsync(app.Client, "  Fire TV de\tCristina \u0007");
        JsonElement shown = (await Json(await ContextAsync(browser, codes.UserCode))).GetProperty("device");
        Assert.Equal("Fire TV de Cristina", shown.GetProperty("name").GetString());

        // The person approving it names it as they see fit, and that is the session's name.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(browser, codes.UserCode, approve: true, name: "Living room TV")).StatusCode);
        JsonElement tokens = await Json(await PollAsync(app.Client, codes.DeviceCode));
        JsonElement access = Claims(tokens.GetProperty("access_token").GetString()!);
        Assert.Equal("Living room TV", access.GetProperty(ApplicationClaims.DeviceName).GetString());
        string sid = access.GetProperty("sid").GetString()!;

        var registry = anchor.Service<SqliteSessionRegistry>();
        IReadOnlyList<SqliteSessionRegistry.LiveSession> live = await registry.ListAsync([user.AsIdentity().Handle]);
        Assert.Equal("Living room TV", live.Single(s => s.SessionId == sid).Name);

        // Every token the session is renewed with carries it, read from the session rather than the old token.
        using HttpClient device = anchor.Following();
        JsonElement renewed = await Json(await device.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = app.Client,
        })));
        Assert.Equal("Living room TV",
            Claims(renewed.GetProperty("access_token").GetString()!).GetProperty(ApplicationClaims.DeviceName).GetString());
    }

    [Fact]
    public async Task A_device_keeps_the_name_it_sent_when_the_person_does_not_change_it()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client, "Fire TV de Cristina");

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(browser, codes.UserCode, approve: true)).StatusCode);
        JsonElement access = Claims((await Json(await PollAsync(app.Client, codes.DeviceCode))).GetProperty("access_token").GetString()!);
        Assert.Equal("Fire TV de Cristina", access.GetProperty(ApplicationClaims.DeviceName).GetString());
    }

    [Fact]
    public async Task A_device_with_no_name_carries_none()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");

        // Sent none; and a name the person clears is none too.
        Codes unnamed = await BeginAsync(app.Client);
        JsonElement shown = (await Json(await ContextAsync(browser, unnamed.UserCode))).GetProperty("device");
        Assert.True(!shown.TryGetProperty("name", out JsonElement name) || name.ValueKind == JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(browser, unnamed.UserCode, approve: true)).StatusCode);
        JsonElement access = Claims((await Json(await PollAsync(app.Client, unnamed.DeviceCode))).GetProperty("access_token").GetString()!);
        Assert.False(access.TryGetProperty(ApplicationClaims.DeviceName, out _));

        Codes cleared = await BeginAsync(app.Client, "Fire TV de Cristina");
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(browser, cleared.UserCode, approve: true, name: "   ")).StatusCode);
        access = Claims((await Json(await PollAsync(app.Client, cleared.DeviceCode))).GetProperty("access_token").GetString()!);
        Assert.False(access.TryGetProperty(ApplicationClaims.DeviceName, out _));
    }

    [Theory]
    [InlineData("Living room TV", "Living room TV")]
    [InlineData("  Living\n\nroom   TV  ", "Living room TV")]
    [InlineData("\u0000\u0007", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_device_name_is_cleaned(string? sent, string? kept) =>
        Assert.Equal(kept, DeviceAuthorization.CleanName(sent));

    [Fact]
    public void A_device_name_is_cut_rather_than_refused()
    {
        string kept = DeviceAuthorization.CleanName(new string('a', 70) + "\U0001F4FA")!;
        Assert.Equal(DeviceAuthorization.MaxNameLength, kept.Length);

        // Cut between characters a person sees, never through one.
        string emoji = string.Concat(Enumerable.Repeat("\U0001F4FA", 70));
        Assert.Equal(DeviceAuthorization.MaxNameLength,
            new System.Globalization.StringInfo(DeviceAuthorization.CleanName(emoji)!).LengthInTextElements);
    }

    private async Task<HttpResponseMessage> RenameAsync(AnchorFixture.Session session, string sid, string? name)
    {
        using HttpClient browser = anchor.Following();
        using HttpRequestMessage request = session.AtTheAccountPage(HttpMethod.Post, "/account/sessions/name", new { sid, name });
        return await browser.SendAsync(request);
    }

    private async Task<JsonElement> RefreshAsync(string client, string refreshToken)
    {
        using HttpClient device = anchor.Following();
        HttpResponseMessage renewed = await device.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = client,
        }));
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        return await Json(renewed);
    }

    [Fact]
    public async Task A_device_is_renamed_from_the_account_page_and_its_next_token_says_so()
    {
        Registered app = await ApplicationAsync();
        (KgsmUser user, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client, "Fire TV de Cristina");
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(browser, codes.UserCode, approve: true)).StatusCode);
        JsonElement tokens = await Json(await PollAsync(app.Client, codes.DeviceCode));
        string sid = Claims(tokens.GetProperty("access_token").GetString()!).GetProperty("sid").GetString()!;

        // Cleaned as at the approval page, and answered as kept.
        HttpResponseMessage renamed = await RenameAsync(browser, sid, "  Bedroom\t TV ");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Bedroom TV", (await Json(renamed)).GetProperty("name").GetString());

        var registry = anchor.Service<SqliteSessionRegistry>();
        Assert.Equal("Bedroom TV",
            (await registry.ListAsync([user.AsIdentity().Handle])).Single(s => s.SessionId == sid).Name);

        // The device learns it at its next refresh; the session goes on as it was.
        JsonElement renewed = await RefreshAsync(app.Client, tokens.GetProperty("refresh_token").GetString()!);
        Assert.Equal("Bedroom TV",
            Claims(renewed.GetProperty("access_token").GetString()!).GetProperty(ApplicationClaims.DeviceName).GetString());

        // Emptied, it has none, and its tokens carry none.
        HttpResponseMessage cleared = await RenameAsync(browser, sid, "   ");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False((await Json(cleared)).TryGetProperty("name", out _));
        renewed = await RefreshAsync(app.Client, renewed.GetProperty("refresh_token").GetString()!);
        Assert.False(Claims(renewed.GetProperty("access_token").GetString()!).TryGetProperty(ApplicationClaims.DeviceName, out _));
    }

    [Fact]
    public async Task Only_your_own_device_is_renamed()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session owner) = await anchor.SignedInAsync(prefix: "device");
        (_, AnchorFixture.Session stranger) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client, "Fire TV de Cristina");
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(owner, codes.UserCode, approve: true)).StatusCode);
        string sid = Claims((await Json(await PollAsync(app.Client, codes.DeviceCode))).GetProperty("access_token").GetString()!)
            .GetProperty("sid").GetString()!;

        HttpResponseMessage theirs = await RenameAsync(stranger, sid, "Mine now");
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);

        // A browser's own sign-in is not a device, and has no name to give.
        HttpResponseMessage browser = await RenameAsync(owner, owner.ProviderSession, "My phone");
        Assert.Equal(HttpStatusCode.BadRequest, browser.StatusCode);
        Assert.Equal("not_a_device", (await Json(browser)).GetProperty("error").GetProperty("code").GetString());

        var registry = anchor.Service<SqliteSessionRegistry>();
        Assert.Equal("Fire TV de Cristina", registry.SessionName(sid));
    }

    [Fact]
    public async Task A_device_signs_out_by_revoking_its_refresh_token()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(browser, codes.UserCode, approve: true)).StatusCode);
        JsonElement tokens = await Json(await PollAsync(app.Client, codes.DeviceCode));
        string refresh = tokens.GetProperty("refresh_token").GetString()!;
        string sid = Claims(tokens.GetProperty("access_token").GetString()!).GetProperty("sid").GetString()!;

        // As an OIDC client library sends it: the token, the hint and the public client's id.
        using HttpClient device = anchor.Following();
        HttpResponseMessage revoked = await device.PostAsync("/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refresh,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = app.Client,
        }));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var registry = anchor.Service<SqliteSessionRegistry>();
        Assert.False(await registry.IsAliveAsync(sid));
        HttpResponseMessage renewed = await device.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = app.Client,
        }));
        Assert.Equal("invalid_grant", await ErrorOf(renewed));

        // The phone that approved it is still signed in.
        Assert.NotNull(await registry.FindProviderSessionAsync(browser.ProviderSession));
    }

    [Fact]
    public async Task A_refused_device_is_told_so_once()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client);

        HttpResponseMessage decided = await DecideAsync(browser, codes.UserCode, approve: false);
        Assert.Equal("denied", (await Json(decided)).GetProperty("decision").GetString());

        Assert.Equal("access_denied", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
        Assert.Equal("invalid_grant", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
    }

    [Fact]
    public async Task A_code_nobody_decided_in_time_expires()
    {
        Registered app = await ApplicationAsync();
        Codes codes = await BeginAsync(app.Client);

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(anchor.Root, "sessions.db")}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE device_authorizations SET expires = $past WHERE client_id = $client;";
            cmd.Parameters.AddWithValue("$past", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            cmd.Parameters.AddWithValue("$client", app.Client);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        Assert.Equal("expired_token", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
        Assert.Equal("invalid_grant", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
    }

    [Fact]
    public async Task Only_a_device_client_asks_for_codes_or_redeems_them()
    {
        Registered browserApp = await ApplicationAsync(device: false);
        HttpResponseMessage refused = await BeginRawAsync(browserApp.Client);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("unauthorized_client", await ErrorOf(refused));

        HttpResponseMessage unknown = await BeginRawAsync("no-such-client");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("invalid_client", await ErrorOf(unknown));

        // Another device client cannot collect a code it was not issued.
        Registered app = await ApplicationAsync();
        Registered other = await ApplicationAsync();
        Codes codes = await BeginAsync(app.Client);
        Assert.Equal("invalid_grant", await ErrorOf(await PollAsync(other.Client, codes.DeviceCode)));
        Assert.Equal("authorization_pending", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
    }

    [Fact]
    public async Task A_device_client_is_public_and_needs_no_redirect()
    {
        string id = "dv" + Guid.NewGuid().ToString("N")[..8];
        ApplicationResult confidential = await anchor.Service<ApplicationRegistry>().AddAsync(
            new ApplicationRequest(id, "Cinema " + id, null, null, null, null, null,
                [new ApplicationClientRequest(id + "-tv", [], [], Confidential: true, Device: true)]),
            "local:test", origin: null, CancellationToken.None);
        Assert.Equal(ApplicationOutcome.Invalid, confidential.Outcome);

        Registered app = await ApplicationAsync();
        Assert.True(anchor.Service<ClientRegistry>().Find(app.Client) is { Device: true, Confidential: false, RedirectUris.Count: 0 });
    }

    // ── The page ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_decision_from_another_site_is_refused_before_anything_is_read()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client);

        using HttpClient client = anchor.Following();
        using HttpRequestMessage request = browser.AtTheAccountPage(HttpMethod.Post, "/device/decision",
            new { userCode = codes.UserCode, approve = true });
        request.Headers.Remove("Sec-Fetch-Site");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        HttpResponseMessage refused = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        Assert.Equal("authorization_pending", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
    }

    [Fact]
    public async Task An_account_waiting_for_approval_cannot_sign_a_device_in()
    {
        Registered app = await ApplicationAsync();
        KgsmUser pending = await anchor.SeedAsync("device-pending-" + Guid.NewGuid().ToString("N")[..8],
            "a long enough password", status: UserStatus.Pending);
        AnchorFixture.Session browser = await anchor.SignInAsync(pending);
        Codes codes = await BeginAsync(app.Client);

        HttpResponseMessage refused = await DecideAsync(browser, codes.UserCode, approve: true);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("authorization_pending", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
    }

    [Fact]
    public async Task An_account_disabled_after_approving_gets_the_device_nothing()
    {
        Registered app = await ApplicationAsync();
        (KgsmUser user, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(browser, codes.UserCode, approve: true)).StatusCode);

        await anchor.Store.UpdateAsync(user with { Status = UserStatus.Disabled, Updated = DateTimeOffset.UtcNow });

        Assert.Equal("access_denied", await ErrorOf(await PollAsync(app.Client, codes.DeviceCode)));
    }

    [Fact]
    public async Task Codes_that_name_no_device_are_limited_per_sign_in()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client);

        for (int i = 0; i < DeviceCodeGuesses.PerSignIn; i++)
        {
            string wrong = DeviceAuthorization.Format(DeviceAuthorization.NewUserCode());
            Assert.Equal(HttpStatusCode.NotFound, (await ContextAsync(browser, wrong)).StatusCode);
        }

        // Even the right code waits now, and the decision door is held to the same count.
        HttpResponseMessage held = await ContextAsync(browser, codes.UserCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, held.StatusCode);
        Assert.NotNull(held.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await DecideAsync(browser, codes.UserCode, approve: true)).StatusCode);

        // Another sign-in is not.
        (_, AnchorFixture.Session other) = await anchor.SignedInAsync(prefix: "device");
        Assert.Equal(HttpStatusCode.OK, (await ContextAsync(other, codes.UserCode)).StatusCode);
    }

    [Fact]
    public async Task A_browser_with_no_sign_in_signs_in_and_comes_back_with_its_code()
    {
        Registered app = await ApplicationAsync();
        KgsmUser user = await anchor.SeedAsync("device-" + Guid.NewGuid().ToString("N")[..8], "a long enough password");
        Codes codes = await BeginAsync(app.Client);

        using HttpClient browser = anchor.Following();
        HttpResponseMessage page = await browser.GetAsync(new Uri(codes.Complete).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("sign in a device", await page.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var credentials = new HttpRequestMessage(HttpMethod.Post, "/authorize/credentials")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = user.Username,
                ["password"] = "a long enough password",
            }),
        };
        credentials.Headers.Add("Sec-Fetch-Site", "same-origin");
        HttpResponseMessage answered = await browser.SendAsync(credentials);
        // Back to the page with the code it arrived with, and nothing else: the sign-in was for this page
        // alone, and no authorization code was issued to anybody.
        Assert.Equal(HttpStatusCode.SeeOther, answered.StatusCode);
        Assert.Equal(codes.Complete, answered.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Without_script_the_floor_confirms_and_decides()
    {
        Registered app = await ApplicationAsync();
        (_, AnchorFixture.Session browser) = await anchor.SignedInAsync(prefix: "device");
        Codes codes = await BeginAsync(app.Client);

        using HttpClient client = anchor.Following();
        using HttpRequestMessage confirm = browser.AtTheAccountPage(HttpMethod.Get, "/device/confirm?user_code=" + codes.UserCode);
        confirm.Headers.Remove("Accept");
        HttpResponseMessage shown = await client.SendAsync(confirm);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        string html = await shown.Content.ReadAsStringAsync();
        Assert.Contains(app.Name, html, StringComparison.Ordinal);
        Assert.Contains("action=\"/device/decision\"", html, StringComparison.Ordinal);

        using var post = new HttpRequestMessage(HttpMethod.Post, "/device/decision")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user_code"] = codes.UserCode,
                ["decision"] = "approve",
            }),
        };
        post.Headers.Add("Cookie", $"{ProviderCookies.Session}={browser.Cookie}");
        post.Headers.Add("Sec-Fetch-Site", "same-origin");
        HttpResponseMessage decided = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Contains("Device signed in", await decided.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await PollAsync(app.Client, codes.DeviceCode)).StatusCode);
    }
}
