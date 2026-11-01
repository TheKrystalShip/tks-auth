using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.Auth.Sessions.Testing;

namespace TheKrystalShip.Auth.Sessions.Tests;

public sealed class SessionsTests : IAsyncLifetime
{
    private const string Audience = "app";
    private const string KeyScheme = "Key";

    private readonly TestIssuer _issuer = new(Audience);
    private readonly List<string> _admitted = [];
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private TimeSpan _later;

    public async Task InitializeAsync()
    {
        _app = Build(_issuer, s => s.AddSessions(o =>
        {
            o.Issuer = _issuer.Issuer;
            o.Audience = Audience;
            o.Admitted = (_, person) => { lock (_admitted) _admitted.Add(person.Subject); };
        }));
        _app.MapGet("/me", (HttpContext c) => Results.Text($"{SessionPerson.Of(c)!.Subject}|{SessionPerson.Of(c)!.DisplayName}|{SessionPerson.Of(c)!.Via}"))
            .RequirePerson();
        _app.MapGet("/start", () => "started").RequireAction("server:start", "server:admin");
        _app.MapGet("/held", (HttpContext c) => Results.Text(
            string.Join(',', SessionPerson.Of(c)!.Actions.Order()) + "|" + SessionPerson.Of(c)!.ExpiresAt.ToString("O"))).RequirePerson();
        _app.MapGet("/until", (HttpContext c) =>
        {
            using var source = SessionPerson.UntilExpiry(c, new LaterTime(() => _later));
            return Results.Text(source.IsCancellationRequested.ToString());
        }).RequirePerson();
        _app.MapGet("/open", () => "nobody").RequireAction();
        _app.MapGet("/keyed", () => "keyed").RequireAuthorization(SessionAccess.PolicyFor(["server:start"], [KeyScheme]));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private static WebApplication Build(TestIssuer issuer, Action<IServiceCollection> sessions, Dictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        if (settings is not null) builder.Configuration.AddInMemoryCollection(settings);
        sessions(builder.Services);
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, KeyHandler>(KeyScheme, null);
        issuer.UseTestIssuer(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    private Task<HttpResponseMessage> GetAsync(string path, string? token, string? key = null) => SendAsync(_client, path, token, key);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string? token, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.Add("X-Key", key);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_valid_token_is_admitted_and_names_the_person()
    {
        var response = await GetAsync("/me", _issuer.Mint("acct-1", "Ada", ["server:start"]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acct-1|Ada|", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_token_without_a_name_shows_the_username_else_the_subject()
    {
        var username = await GetAsync("/me", _issuer.Mint("acct-1", name: "", claims: new Dictionary<string, string> { ["preferred_username"] = "ada" }));
        Assert.Equal("acct-1|ada|", await username.Content.ReadAsStringAsync());

        var bare = await GetAsync("/me", _issuer.Mint("acct-1", name: ""));
        Assert.Equal("acct-1|acct-1|", await bare.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_token_minted_for_a_client_acting_for_the_person_names_that_client()
    {
        var response = await GetAsync("/me", _issuer.Mint("acct-1", "Ada", actor: "app-bot"));
        Assert.Equal("acct-1|Ada|app-bot", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_service_hears_of_each_person_its_tokens_admit()
    {
        await GetAsync("/me", _issuer.Mint("acct-7"));
        lock (_admitted) Assert.Contains("acct-7", _admitted);
    }

    [Fact]
    public async Task No_token_is_a_401_with_a_bearer_challenge_in_the_error_shape()
    {
        var response = await GetAsync("/me", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Contains("\"error\":{\"code\":\"unauthorized\"", await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<string> Refused => ["audience", "issuer", "expired", "unsigned", "foreign-key", "rsa", "id-token"];

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_token_that_fails_validation_is_a_401(string flaw)
    {
        var token = flaw switch
        {
            "audience" => _issuer.Mint("a", audience: "other"),
            "issuer" => _issuer.Mint("a", issuer: "https://elsewhere.test"),
            "expired" => _issuer.Mint("a", lifetime: TimeSpan.FromMinutes(-5)),
            "unsigned" => Unsigned(_issuer.Mint("a")),
            "foreign-key" => _issuer.Mint("a", signedBy: new TestIssuer(Audience)),
            "rsa" => _issuer.Mint("a", rsa: true),
            _ => _issuer.Mint("a", type: "JWT"),
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/me", token)).StatusCode);
    }

    [Fact]
    public async Task A_token_listing_one_of_the_routes_actions_is_admitted()
    {
        var response = await GetAsync("/start", _issuer.Mint("a", actions: ["server:admin"]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_person_who_may_not_is_a_403_in_the_error_shape()
    {
        var response = await GetAsync("/start", _issuer.Mint("a", actions: ["server:view"]));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"code\":\"forbidden\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_route_naming_no_action_admits_nobody()
    {
        var response = await GetAsync("/open", _issuer.Mint("a", actions: ["server:start"]));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("key", null, HttpStatusCode.OK)]
    [InlineData(null, "server:start", HttpStatusCode.OK)]
    [InlineData(null, "server:view", HttpStatusCode.Forbidden)]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    public async Task A_route_taking_another_scheme_admits_its_identity_or_a_person_holding_the_action(
        string? key, string? action, HttpStatusCode expected)
    {
        var token = action is null ? null : _issuer.Mint("a", actions: [action]);
        Assert.Equal(expected, (await GetAsync("/keyed", token, key)).StatusCode);
    }

    [Fact]
    public async Task Another_scheme_admits_nothing_on_a_route_that_does_not_take_it()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/start", null, key: "key")).StatusCode);
    }

    [Fact]
    public async Task The_person_carries_the_actions_and_the_expiry_of_the_token()
    {
        var response = await GetAsync("/held", _issuer.Mint("a", actions: ["x", "y"], lifetime: TimeSpan.FromMinutes(5)));
        var parts = (await response.Content.ReadAsStringAsync()).Split('|');
        Assert.Equal("x,y", parts[0]);
        Assert.InRange(DateTimeOffset.Parse(parts[1]), DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(10, true)]
    public async Task A_stream_is_cancelled_once_its_token_has_expired(int minutesLater, bool cancelled)
    {
        _later = TimeSpan.FromMinutes(minutesLater);
        var response = await GetAsync("/until", _issuer.Mint("a"));
        Assert.Equal(cancelled.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_service_reads_its_own_section_over_its_defaults_and_writes_its_own_refusals()
    {
        await using var app = Build(_issuer, s => s.AddSessions(section: "Oidc", defaults: o =>
            {
                o.Audience = "wrong";
                o.WriteRefusal = (http, refusal) => http.Response.WriteAsync(refusal == Refusal.SignIn ? "sign in here" : "not you");
            }),
            new() { ["Oidc:Issuer"] = _issuer.Issuer, ["Oidc:Audience"] = Audience });
        app.MapGet("/start", () => "started").RequireAction("server:start");
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "/start", _issuer.Mint("a", actions: ["server:start"]))).StatusCode);

        var signIn = await SendAsync(client, "/start", null);
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Contains(signIn.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Equal("sign in here", await signIn.Content.ReadAsStringAsync());

        var forbidden = await SendAsync(client, "/start", _issuer.Mint("a", actions: ["server:view"]));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("not you", await forbidden.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_person_holds_only_the_actions_the_service_declares()
    {
        await using var app = Build(_issuer, s => s.AddSessions(o =>
        {
            o.Issuer = _issuer.Issuer;
            o.Audience = Audience;
            o.DeclaredActions = new HashSet<string> { "app:read" };
        }));
        app.MapGet("/held", (HttpContext c) => string.Join(',', SessionPerson.Of(c)!.Actions)).RequireAction("app:read");
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await SendAsync(client, "/held", _issuer.Mint("a", actions: ["app:read", "other:write"]));
        Assert.Equal("app:read", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_host_whose_route_checks_an_undeclared_action_refuses_to_start()
    {
        await using var app = Build(_issuer, s => s.AddSessions(o =>
        {
            o.Issuer = _issuer.Issuer;
            o.Audience = Audience;
            o.DeclaredActions = new HashSet<string> { "app:read" };
        }));
        app.MapGet("/read", () => "read").RequireAction("app:read");
        app.MapGet("/write", () => "write").RequireAction("app:write");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("app:write", refused.Message);
        Assert.DoesNotContain("app:read", refused.Message);
    }

    [Fact]
    public async Task The_test_token_endpoint_records_each_exchange_and_answers_as_the_test_says()
    {
        _issuer.Exchange = form => (HttpStatusCode.OK, TestIssuer.Minted("minted-" + form["subject_token"]));
        using var http = new HttpClient(_issuer.Handler());
        using var request = new HttpRequestMessage(HttpMethod.Post, _issuer.Issuer + "/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["subject_token"] = "abc def" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String("client-1:secret"u8.ToArray()));

        var response = await http.SendAsync(request);

        Assert.Contains("\"access_token\":\"minted-abc def\"", await response.Content.ReadAsStringAsync());
        var exchange = Assert.Single(_issuer.Exchanges);
        Assert.Equal("client-1", exchange.Client);
        Assert.Equal("abc def", exchange.Form["subject_token"]);
    }

    [Fact]
    public async Task An_issuer_sharing_anothers_keys_mints_tokens_the_other_admits()
    {
        var sharing = new TestIssuer(Audience, keysOf: _issuer);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/me", sharing.Mint("a"))).StatusCode);
    }

    private sealed class LaterTime(Func<TimeSpan> later) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + later();
    }

    /// <summary>A scheme of a service's own, proved by an <c>X-Key</c> header.</summary>
    private sealed class KeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.TryGetValue("X-Key", out var key) && key == "key"
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("key", "yes")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private static string Unsigned(string token)
    {
        var parts = token.Split('.');
        return parts[0] + "." + parts[1] + ".";
    }
}
