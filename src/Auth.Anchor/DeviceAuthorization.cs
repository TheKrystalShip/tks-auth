using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Anchor;

/// <summary>
/// The device authorization grant (RFC 8628): a screen with no browser to bounce — a television — asks for
/// a pair of codes, shows the short one, and waits at <c>/token</c> while a person signed in here on
/// another device approves it at <c>/device</c> (<see cref="DevicePageEndpoints"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a client registered for it.</b> A client of an application marked
/// <see cref="RegisteredClient.Device"/>, which is public: a device keeps no secret. Any other client is
/// <c>unauthorized_client</c>.
/// </para>
/// <para>
/// <b>The device's session is a sign-in of its own.</b> It is minted through the one mint site, recording
/// the sign-in, under no provider session and carrying <see cref="SqliteSessionRegistry.DeviceKind"/>:
/// signing out of the browser that approved it does not sign the television out. It is listed on the
/// account page beside the others, and ending it there, or disabling the account, ends it.
/// </para>
/// <para>
/// <b>The account decides when the device redeems, not only when the person approves.</b> An approval is
/// for whoever approved it, and that account is resolved again when the device collects: an account
/// disabled in between gets the device nothing.
/// </para>
/// </remarks>
internal static class DeviceAuthorization
{
    /// <summary>The grant type a device polls <c>/token</c> with (RFC 8628 §3.4).</summary>
    internal const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Where a person enters the user code, on the issuer's origin.</summary>
    internal const string VerificationPath = "/device";

    /// <summary>How long a pair of codes waits for a person.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How often a device may ask, until it is told to slow down.</summary>
    internal const int IntervalSeconds = 5;

    /// <summary>What every <c>slow_down</c> adds to the interval (RFC 8628 §3.5).</summary>
    internal const int SlowDownStepSeconds = 5;

    /// <summary>
    /// The characters a user code is drawn from: consonants a person reads off a television and types on a
    /// phone, with no vowel to spell a word and nothing that looks like a digit.
    /// </summary>
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    /// <summary>
    /// A user code's length. Eight from twenty is about 2.6×10¹⁰ codes, against attempts the page limits
    /// (<see cref="DeviceCodeGuesses"/>) over a code's ten minutes.
    /// </summary>
    private const int UserCodeLength = 8;

    /// <summary>The largest form these doors read.</summary>
    private const int MaxFormBytes = 8 * 1024;

    /// <summary>
    /// The extension parameter (RFC 8628 §3.1 allows them) a device names itself with: the name its owner
    /// gave it, which the person approving it sees and may change.
    /// </summary>
    internal const string NameParameter = "device_name";

    /// <summary>The longest a device's name is kept, in characters a person sees.</summary>
    internal const int MaxNameLength = 64;

    /// <summary>
    /// <c>POST /device_authorization</c> (RFC 8628 §3.1): a device asks for its codes.
    /// </summary>
    internal static async Task Begin(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";

        AnchorOptions options = ctx.RequestServices.GetRequiredService<AnchorOptions>();
        if (options.IssuerUrl is null)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "This anchor has no issuer address, so it signs nobody in.");
            return;
        }

        if (!ctx.RequestServices.GetRequiredService<AnchorRole>().IsAuthority)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "This member does not hold the cluster's accounts.");
            return;
        }

        if (!ctx.Request.HasFormContentType || ctx.Request.ContentLength > MaxFormBytes)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_request",
                "The request is a form of at most 8 KiB.");
            return;
        }

        IFormCollection form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var auth = ClientAuthentication.Read(ctx.Request, form, ctx.RequestServices.GetRequiredService<ClientRegistry>());
        if (auth.Result != ClientAuthentication.Outcome.Unauthenticated
            || auth.ClientId is not { } clientId
            || ctx.RequestServices.GetRequiredService<ClientRegistry>().Find(clientId) is not { } client)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status401Unauthorized, "invalid_client",
                "A device names its client with client_id and authenticates with nothing else.");
            return;
        }

        if (DeviceClientProblem(ctx, client) is { } refusal)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "unauthorized_client", refusal);
            return;
        }

        if (OidcEndpoints.Single(form, "scope") is { } scope
            && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(s => s != "openid"))
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_scope",
                "The one scope served is openid.");
            return;
        }

        var registry = ctx.RequestServices.GetRequiredService<SqliteSessionRegistry>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string deviceCode = ProviderCookies.NewSecret();
        string? name = CleanName(OidcEndpoints.Single(form, NameParameter));

        // A user code is short enough to collide with one another device is showing. The store refuses the
        // second, and another is drawn; a run of collisions this long means the table is full of them.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string userCode = NewUserCode();
            if (!await registry.CreateDeviceAuthorizationAsync(
                    ProviderCookies.Hash(deviceCode), HashUserCode(userCode), client.ClientId, Endpoints.UserAgentOf(ctx),
                    name, now, now.Add(Lifetime), IntervalSeconds, ctx.RequestAborted))
                continue;

            string verification = options.Issuer + VerificationPath;
            await Endpoints.WriteJson(ctx, StatusCodes.Status200OK, new DeviceAuthorizationResponse(
                DeviceCode: deviceCode,
                UserCode: Format(userCode),
                VerificationUri: verification,
                VerificationUriComplete: verification + "?user_code=" + Format(userCode),
                ExpiresIn: (long)Lifetime.TotalSeconds,
                Interval: IntervalSeconds),
                AnchorJsonContext.Default.DeviceAuthorizationResponse);
            return;
        }

        await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
            "No user code could be drawn. Try again shortly.");
    }

    /// <summary>
    /// <c>POST /token</c> with the device code grant (RFC 8628 §3.4): a device asking whether a person has
    /// decided.
    /// </summary>
    internal static async Task RedeemAsync(HttpContext ctx, IFormCollection form, ClientAuthentication auth)
    {
        IServiceProvider services = ctx.RequestServices;

        if (OidcEndpoints.Single(form, "device_code") is not { } deviceCode || auth.ClientId is not { } clientId)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_request",
                "device_code and client_id are required.");
            return;
        }

        if (services.GetRequiredService<ClientRegistry>().Find(clientId) is not { } client
            || !auth.Speaks(client))
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status401Unauthorized, "invalid_client",
                "A device names its client with client_id and authenticates with nothing else.");
            return;
        }

        if (DeviceClientProblem(ctx, client) is not null
            || services.GetRequiredService<ApplicationRegistry>().Of(client) is not { } application)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "unauthorized_client",
                "This client does not sign in with the device grant.");
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        (DevicePoll outcome, ApprovedDevice? approval) = await services.GetRequiredService<SqliteSessionRegistry>()
            .PollDeviceAsync(ProviderCookies.Hash(deviceCode), client.ClientId, now, SlowDownStepSeconds, ctx.RequestAborted);

        switch (outcome)
        {
            case DevicePoll.Pending:
                await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "authorization_pending",
                    "Nobody has approved this device yet.");
                return;

            case DevicePoll.SlowDown:
                await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "slow_down",
                    "Asking too often: wait five seconds longer between requests.");
                return;

            case DevicePoll.Denied:
                await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "access_denied",
                    "The person refused this device.");
                return;

            case DevicePoll.Expired:
                await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "expired_token",
                    "Nobody approved this device in time. Start again.");
                return;

            case DevicePoll.Approved:
                await MintAsync(ctx, approval!, client, application, now);
                return;

            default:
                await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_grant",
                    "That device code is not valid for this client.");
                return;
        }
    }

    /// <summary>The approved device's session, for the account that approved it as it stands now.</summary>
    private static async Task MintAsync(
        HttpContext ctx, ApprovedDevice approval, RegisteredClient client, Application application, DateTimeOffset now)
    {
        if (StoredIdentity.Read(approval.IdentityJson) is not { } stored)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "access_denied",
                "The approval for this device cannot be read. Start again.");
            return;
        }

        KgsmIdentity identity = stored.ToIdentity();
        AccountAnswer standing;
        try
        {
            standing = await ctx.RequestServices.GetRequiredService<AccountResolver>().ResolveAsync(identity, ctx.RequestAborted);
        }
        catch (KgsmAuthProviderException)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "The account store could not be read.");
            return;
        }

        if (standing is not { Outcome: AccountOutcome.Ok, User: { Status: UserStatus.Active } user }
            || !string.Equals(user.UserId, approval.UserId, StringComparison.Ordinal))
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "access_denied",
                "The account that approved this device cannot be signed in to.");
            return;
        }

        bool minted = await Endpoints.MintSessionFor(ctx, identity, user, now, (access, refresh) =>
            Endpoints.WriteJson(ctx, StatusCodes.Status200OK, new TokenResponse(
                AccessToken: access.Token,
                TokenType: "Bearer",
                ExpiresIn: Math.Max(0, (long)(access.ExpiresAt - now).TotalSeconds),
                RefreshToken: refresh.Token,
                IdToken: null,
                Scope: "openid"),
                AnchorJsonContext.Default.TokenResponse),
            providerSession: null, client, application, SqliteSessionRegistry.DeviceKind, approval.Name);

        if (!minted)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "The account store could not be read.");
        }
    }

    /// <summary>Why a client may not use the device grant, or null when it may.</summary>
    internal static string? DeviceClientProblem(HttpContext ctx, RegisteredClient client)
    {
        if (!client.Device || client.Confidential)
            return "This client does not sign in with the device grant.";

        return ctx.RequestServices.GetRequiredService<ApplicationRegistry>().Of(client) is not null
            ? null
            : "The device grant signs in to an application's client.";
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A device's name as it is kept: control characters dropped, every run of white space one space,
    /// trimmed, and cut to <see cref="MaxNameLength"/> characters a person sees — or null when nothing is
    /// left.
    /// </summary>
    /// <remarks>
    /// A name too long is cut rather than refused: a device sends what its system calls it, and refusing it
    /// would refuse the sign-in over a label the person can change on the next page anyway.
    /// </remarks>
    internal static string? CleanName(string? sent)
    {
        if (sent is null)
            return null;

        var kept = new StringBuilder(sent.Length);
        bool space = false;
        foreach (char c in sent)
        {
            if (char.IsWhiteSpace(c))
            {
                space = kept.Length > 0;
                continue;
            }
            if (char.IsControl(c))
                continue;
            if (space)
                kept.Append(' ');
            space = false;
            kept.Append(c);
        }

        if (kept.Length == 0)
            return null;

        string name = kept.ToString();
        var elements = new StringInfo(name);
        return elements.LengthInTextElements <= MaxNameLength
            ? name
            : elements.SubstringByTextElements(0, MaxNameLength).TrimEnd();
    }

    // ── User codes ────────────────────────────────────────────────────────────

    /// <summary>A fresh user code, unformatted.</summary>
    internal static string NewUserCode()
    {
        var code = new StringBuilder(UserCodeLength);
        for (int i = 0; i < UserCodeLength; i++)
            code.Append(UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)]);
        return code.ToString();
    }

    /// <summary>
    /// A user code as a person typed it — any case, with or without its dash or spaces — reduced to the
    /// stored form, or null when it cannot be one.
    /// </summary>
    internal static string? Normalize(string? typed)
    {
        if (typed is null || typed.Length > 32)
            return null;

        var code = new StringBuilder(UserCodeLength);
        foreach (char c in typed)
        {
            if (c is '-' or ' ')
                continue;
            char upper = char.ToUpperInvariant(c);
            if (!UserCodeAlphabet.Contains(upper))
                return null;
            code.Append(upper);
        }

        return code.Length == UserCodeLength ? code.ToString() : null;
    }

    /// <summary>A user code as a device shows it: two groups of four.</summary>
    internal static string Format(string code) => code[..4] + "-" + code[4..];

    /// <summary>The key a normalized user code is stored under.</summary>
    internal static string HashUserCode(string normalized) => ProviderCookies.Hash("user-code:" + normalized);
}
