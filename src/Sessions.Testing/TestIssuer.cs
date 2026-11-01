using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TheKrystalShip.Auth.Sessions.Testing;

/// <summary>
/// A provider for tests: discovery, a key set holding an ES256 key and an RSA one, a token endpoint whose
/// exchanges a test answers, and access tokens minted the way the provider mints them. One instance owns
/// its keys, so two instances never admit each other's tokens.
/// </summary>
public sealed class TestIssuer
{
    public const string DefaultIssuer = "https://auth.test";

    /// <summary>The token type an exchange's answer says it issued.</summary>
    public const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    private readonly ECDsa _ecdsa;
    private readonly RSA _rsa;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly List<(string Client, IReadOnlyDictionary<string, string> Form)> _exchanges = [];

    /// <param name="audience">The audience tokens are minted for unless a mint names another.</param>
    /// <param name="issuer">The issuer discovery reports and tokens carry in <c>iss</c>.</param>
    /// <param name="actionsClaim">The claim a token lists its actions in.</param>
    /// <param name="keysOf">
    /// An issuer whose keys this one signs with and serves, so tokens either mints are good at a host trusting
    /// the other, while each answers its own token endpoint. Unset, the issuer has keys of its own.
    /// </param>
    public TestIssuer(string audience, string issuer = DefaultIssuer, string actionsClaim = "tks_actions", TestIssuer? keysOf = null)
    {
        _ecdsa = keysOf?._ecdsa ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _rsa = keysOf?._rsa ?? RSA.Create(2048);
        Audience = audience;
        Issuer = issuer;
        ActionsClaim = actionsClaim;
    }

    /// <summary>The audience tokens are minted for unless a mint names another.</summary>
    public string Audience { get; }

    /// <summary>The issuer discovery reports and tokens carry in <c>iss</c>.</summary>
    public string Issuer { get; }

    /// <summary>The claim a token lists its actions in.</summary>
    public string ActionsClaim { get; }

    /// <summary>How the token endpoint answers an exchange, from its form fields: a status and a JSON body. A refusal unless set.</summary>
    public Func<IReadOnlyDictionary<string, string>, (HttpStatusCode Status, string Body)> Exchange { get; set; } =
        _ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

    /// <summary>The exchanges asked of the token endpoint, as their form fields, with the client that authenticated.</summary>
    public IReadOnlyList<(string Client, IReadOnlyDictionary<string, string> Form)> Exchanges
    {
        get { lock (_exchanges) return [.. _exchanges]; }
    }

    /// <summary>
    /// An access token as the provider mints one, with whatever a test changes. <paramref name="lifetime"/>
    /// is measured from now and may be negative for an expired token; <paramref name="signedBy"/> signs it
    /// with a different issuer's keys; <paramref name="actor"/> is the <c>act</c> claim's client;
    /// <paramref name="claims"/> adds string claims of a service's own.
    /// </summary>
    public string Mint(
        string sub, string name = "Someone", IEnumerable<string>? actions = null, string? audience = null,
        string? issuer = null, string type = "at+jwt", TimeSpan? lifetime = null, bool rsa = false,
        TestIssuer? signedBy = null, string? actor = null, string? clientId = null,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        var keys = signedBy ?? this;
        var now = DateTime.UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["name"] = name,
            [ActionsClaim] = (actions ?? []).ToArray(),
        };
        if ((clientId ?? actor) is { } client) payload["client_id"] = client;
        if (actor is not null) payload["act"] = new Dictionary<string, object> { ["sub"] = actor };
        foreach (var (key, value) in claims ?? new Dictionary<string, string>()) payload[key] = value;

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            Claims = payload,
            IssuedAt = now - TimeSpan.FromMinutes(10),
            NotBefore = now - TimeSpan.FromMinutes(10),
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(5)),
            TokenType = type,
            SigningCredentials = rsa
                ? new SigningCredentials(new RsaSecurityKey(keys._rsa) { KeyId = "rsa" }, SecurityAlgorithms.RsaSha256)
                : new SigningCredentials(new ECDsaSecurityKey(keys._ecdsa) { KeyId = "ec" }, SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>A token exchange's answer carrying <paramref name="token"/>, as the provider writes it.</summary>
    public static string Minted(string token) => JsonSerializer.Serialize(
        new ExchangeAnswer(token, AccessTokenType, "Bearer", 300), TestIssuerJsonContext.Default.ExchangeAnswer);

    /// <summary>The handler that answers discovery, the key set and the token endpoint, for a consumer's backchannel.</summary>
    public HttpMessageHandler Handler() => new Endpoints(this);

    /// <summary>
    /// Points the JwtBearer backchannel of a test host at this issuer, so discovery and keys are served
    /// in-process. Call it on the host's services after <c>AddSessions</c>.
    /// </summary>
    public IServiceCollection UseTestIssuer(IServiceCollection services) =>
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            o => o.BackchannelHttpHandler = Handler());

    private string Discovery() => JsonSerializer.Serialize(new DiscoveryDocument(
        Issuer, Issuer + "/jwks", Issuer + "/authorize", Issuer + "/token", ["ES256"]),
        TestIssuerJsonContext.Default.DiscoveryDocument);

    private string KeySet()
    {
        var ec = _ecdsa.ExportParameters(false);
        var rsa = _rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new KeySetDocument([
            new KeyDocument("EC", "ec", "sig", "ES256", Crv: "P-256",
                X: Base64UrlEncoder.Encode(ec.Q.X!), Y: Base64UrlEncoder.Encode(ec.Q.Y!)),
            new KeyDocument("RSA", "rsa", "sig", "RS256",
                N: Base64UrlEncoder.Encode(rsa.Modulus!), E: Base64UrlEncoder.Encode(rsa.Exponent!)),
        ]), TestIssuerJsonContext.Default.KeySetDocument);
    }

    private sealed class Endpoints(TestIssuer issuer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/openid-configuration") return Json(HttpStatusCode.OK, issuer.Discovery());
            if (path == "/jwks") return Json(HttpStatusCode.OK, issuer.KeySet());
            if (path != "/token" || request.Method != HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.NotFound);

            var client = request.Headers.Authorization is { Scheme: "Basic", Parameter: { } basic }
                ? Encoding.UTF8.GetString(Convert.FromBase64String(basic)).Split(':')[0]
                : "";
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(
                    p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
                    p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
            lock (issuer._exchanges) issuer._exchanges.Add((client, form));

            var (status, body) = issuer.Exchange(form);
            return Json(status, body);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

internal sealed record DiscoveryDocument(
    string Issuer,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] string[] SigningAlgorithms);

internal sealed record KeySetDocument(KeyDocument[] Keys);

internal sealed record KeyDocument(
    string Kty, string Kid, string Use, string Alg,
    string? Crv = null, string? X = null, string? Y = null, string? N = null, string? E = null);

internal sealed record ExchangeAnswer(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("issued_token_type")] string IssuedTokenType,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DiscoveryDocument))]
[JsonSerializable(typeof(KeySetDocument))]
[JsonSerializable(typeof(ExchangeAnswer))]
internal sealed partial class TestIssuerJsonContext : JsonSerializerContext;
