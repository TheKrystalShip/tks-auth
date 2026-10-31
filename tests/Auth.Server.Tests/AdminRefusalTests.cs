using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>
/// The admin pages' client is issued only to accounts that administer, and every refusal at an
/// administrator-only surface is journaled, one line per (surface, account, address) per minute.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class AdminRefusalTests(ServerFixture anchor)
{
    private const string Password = "a long enough password";
    private const string Pages = ServerFixture.Issuer + Application.ProviderPagesPath;

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A browser asks for a code for <paramref name="client"/> and posts <paramref name="user"/>'s credentials.</summary>
    private async Task<(Uri Location, string Verifier)> AuthorizeAsync(UserAccount user, string client, string redirect, string state)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        HttpClient browser = anchor.Following();
        string authorize = QueryHelpers.AddQueryString("/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = client,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = "openid",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(authorize)).StatusCode);

        var post = new HttpRequestMessage(HttpMethod.Post, "/authorize/credentials")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = user.Username,
                ["password"] = Password,
            }),
        };
        post.Headers.Add("Sec-Fetch-Site", "same-origin");
        HttpResponseMessage answer = await browser.SendAsync(post);
        Assert.Equal(HttpStatusCode.SeeOther, answer.StatusCode);
        return (answer.Headers.Location!, verifier);
    }

    private async Task<HttpResponseMessage> ExchangeAsync(string code, string verifier, string client, string redirect) =>
        await anchor.Client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = client,
            ["code_verifier"] = verifier,
        }));

    private async Task<HttpResponseMessage> RefreshAsync(string refresh, string client) =>
        await anchor.Client.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = client,
        }));

    private async Task<AuthorityWrite> EditAsync(string by, AuthorityEdit edit) =>
        await anchor.Store.ApplyAsync(by, edit, await anchor.Store.VersionAsync(), DateTimeOffset.UtcNow);

    /// <summary>An account holding <c>auth:roles.edit</c> cluster-wide, and the assignment that gives it.</summary>
    private async Task<(UserAccount Holder, string AssignmentId, string OwnerId)> AdministratorAsync()
    {
        UserAccount owner = await anchor.SeedAsync(Unique("refusal-root"), Password, owner: true);
        UserAccount holder = await anchor.SeedAsync(Unique("refusal-admin"), Password);
        await anchor.Store.ReplaceCatalogAsync([.. AuthActions.Declared], DateTimeOffset.UtcNow);

        string permission = (await EditAsync(owner.UserId, new CreatePermission(Unique("Edit")))).CreatedId!;
        await EditAsync(owner.UserId, new SetPermissionActions(permission, new HashSet<string> { AuthActions.RolesEdit }));
        string role = (await EditAsync(owner.UserId, new CreateRole(Unique("Editors")))).CreatedId!;
        await EditAsync(owner.UserId, new SetRolePermissions(role, new HashSet<string> { permission }));
        string assignment = (await EditAsync(owner.UserId, new Assign(holder.UserId, role))).CreatedId!;
        return (holder, assignment, owner.UserId);
    }

    private IEnumerable<JsonElement> Refusals(string account) =>
        anchor.Journal(AuthEvents.AdminRefused).Where(e => e.GetProperty("Data").GetProperty("Account").GetString() == account);

    // ── The client's token ────────────────────────────────────────────────────

    [Fact]
    public async Task A_non_administrator_asking_for_the_admin_client_is_sent_back_access_denied()
    {
        UserAccount person = await anchor.SeedAsync(Unique("refusal-person"), Password);

        var (location, _) = await AuthorizeAsync(person, Application.ProviderClientId, Pages, state: "keep-me");

        Assert.StartsWith(Pages, location.ToString(), StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal("keep-me", query["state"].ToString());
        Assert.False(query.ContainsKey("code"));
    }

    [Fact]
    public async Task An_administrator_asking_for_the_admin_client_gets_a_code()
    {
        var (holder, _, _) = await AdministratorAsync();

        var (location, verifier) = await AuthorizeAsync(holder, Application.ProviderClientId, Pages, state: "s");
        var query = QueryHelpers.ParseQuery(location.Query);

        Assert.False(query.ContainsKey("error"));
        HttpResponseMessage token = await ExchangeAsync(query["code"].ToString(), verifier, Application.ProviderClientId, Pages);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
    }

    [Fact]
    public async Task An_administrator_demoted_after_signing_in_is_refused_at_the_next_refresh()
    {
        var (holder, assignment, ownerId) = await AdministratorAsync();
        var (location, verifier) = await AuthorizeAsync(holder, Application.ProviderClientId, Pages, state: "s");
        HttpResponseMessage token = await ExchangeAsync(
            QueryHelpers.ParseQuery(location.Query)["code"].ToString(), verifier, Application.ProviderClientId, Pages);
        string refresh = JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("refresh_token").GetString()!;

        HttpResponseMessage renewed = await RefreshAsync(refresh, Application.ProviderClientId);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        refresh = JsonDocument.Parse(await renewed.Content.ReadAsStringAsync()).RootElement.GetProperty("refresh_token").GetString()!;

        await EditAsync(ownerId, new Revoke(assignment));

        HttpResponseMessage refused = await RefreshAsync(refresh, Application.ProviderClientId);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_grant",
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
        Assert.Contains(Refusals(holder.Username), e => e.GetProperty("Data").GetProperty("Surface").GetString() == "token");
    }

    [Fact]
    public async Task Another_client_still_gets_a_code_for_a_non_administrator()
    {
        UserAccount owner = await anchor.SeedAsync(Unique("refusal-root"), Password, owner: true);
        string ownerToken = (await anchor.SignInAsync(owner)).Access;
        string id = "tst" + Guid.NewGuid().ToString("N")[..8];
        string redirect = $"https://{id}.test/signin";
        using var register = new HttpRequestMessage(HttpMethod.Post, "/auth/admin/applications")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                id,
                name = "Test " + id,
                clients = new[] { new { clientId = id + "-site", redirectUris = new[] { redirect } } },
            }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        register.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        Assert.Equal(HttpStatusCode.Created, (await anchor.Client.SendAsync(register)).StatusCode);

        UserAccount person = await anchor.SeedAsync(Unique("refusal-person"), Password);
        var (location, _) = await AuthorizeAsync(person, id + "-site", redirect, state: "s");

        Assert.True(QueryHelpers.ParseQuery(location.Query).ContainsKey("code"));
    }

    // ── The journal, by surface ───────────────────────────────────────────────

    [Fact]
    public async Task A_refused_token_request_is_journaled_with_the_account()
    {
        UserAccount person = await anchor.SeedAsync(Unique("refusal-person"), Password);

        await AuthorizeAsync(person, Application.ProviderClientId, Pages, state: "s");

        JsonElement data = Assert.Single(Refusals(person.Username)).GetProperty("Data");
        Assert.Equal(AdminRefusalSurfaces.Token, data.GetProperty("Surface").GetString());
        Assert.Equal(1, data.GetProperty("Count").GetInt32());
    }

    [Fact]
    public async Task A_refused_admin_document_names_the_account_holding_the_session()
    {
        var (user, session) = await anchor.SignedInAsync(prefix: "refusal-doc");

        Assert.Equal(HttpStatusCode.NotFound,
            (await anchor.Client.SendAsync(session.AtTheAccountPage(HttpMethod.Get, "/admin/"))).StatusCode);

        JsonElement named = Assert.Single(Refusals(user.Username)).GetProperty("Data");
        Assert.Equal(AdminRefusalSurfaces.Document, named.GetProperty("Surface").GetString());
        Assert.Equal("/admin/", named.GetProperty("Route").GetString());
    }

    [Fact]
    public async Task A_403_at_an_admin_only_route_is_journaled_and_one_at_another_route_is_not()
    {
        var (user, session) = await anchor.SignedInAsync(prefix: "refusal-route");

        using var admin = new HttpRequestMessage(HttpMethod.Get, "/auth/admin/applications");
        admin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Access);
        Assert.Equal(HttpStatusCode.Forbidden, (await anchor.Client.SendAsync(admin)).StatusCode);

        using var shared = new HttpRequestMessage(HttpMethod.Get, "/auth/admin/users");
        shared.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Access);
        Assert.Equal(HttpStatusCode.Forbidden, (await anchor.Client.SendAsync(shared)).StatusCode);

        JsonElement data = Assert.Single(Refusals(user.Username)).GetProperty("Data");
        Assert.Equal(AdminRefusalSurfaces.Route, data.GetProperty("Surface").GetString());
        Assert.Equal("/auth/admin/applications", data.GetProperty("Route").GetString());
    }

    // ── Coalescing ────────────────────────────────────────────────────────────

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Refusals_inside_a_minute_write_one_line_and_a_follow_up_carrying_the_rest()
    {
        var time = new ManualTime();
        using var refusals = new AdminRefusals(anchor.Service<AuditLog>(), time);
        string who = Unique("coalesce");

        for (int i = 0; i < 3; i++)
            await refusals.RecordAsync(AdminRefusalSurfaces.Document, "/admin/", who, "203.0.113.9");
        await refusals.RecordAsync(AdminRefusalSurfaces.Document, "/admin/", who, "203.0.113.10");
        await refusals.RecordAsync(AdminRefusalSurfaces.Token, "/token", who, "203.0.113.9");

        Assert.Equal(3, Refusals(who).Count());

        await refusals.SweepAsync();
        Assert.Equal(3, Refusals(who).Count());

        time.Advance(TimeSpan.FromSeconds(61));
        await refusals.SweepAsync();

        var counts = Refusals(who)
            .Where(e => e.GetProperty("Data").GetProperty("Address").GetString() == "203.0.113.9"
                && e.GetProperty("Data").GetProperty("Surface").GetString() == "document")
            .Select(e => e.GetProperty("Data").GetProperty("Count").GetInt32())
            .ToArray();
        Assert.Equal([1, 2], counts);
        Assert.Equal(4, Refusals(who).Count());
    }

    [Fact]
    public async Task A_refusal_after_the_window_writes_a_fresh_line_and_closes_the_old_window()
    {
        var time = new ManualTime();
        using var refusals = new AdminRefusals(anchor.Service<AuditLog>(), time);
        string who = Unique("coalesce");

        await refusals.RecordAsync(AdminRefusalSurfaces.Document, "/admin/", who, null);
        await refusals.RecordAsync(AdminRefusalSurfaces.Document, "/admin/", who, null);
        time.Advance(TimeSpan.FromSeconds(61));
        await refusals.RecordAsync(AdminRefusalSurfaces.Document, "/admin/", who, null);

        Assert.Equal([1, 1, 1],
            Refusals(who).Select(e => e.GetProperty("Data").GetProperty("Count").GetInt32()).ToArray());
        Assert.Equal(3, Refusals(who).Count());
        Assert.Equal(JsonValueKind.Null, Refusals(who).First().GetProperty("Data").GetProperty("Address").ValueKind);
    }
}
