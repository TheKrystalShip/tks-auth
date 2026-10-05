using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Anchor;

/// <summary>
/// User codes that named no device, counted per browser sign-in and across the provider, so the short
/// code a television shows cannot be found by trying them.
/// </summary>
/// <remarks>
/// <para>
/// Only misses count. A person who types their code right is never slowed; one who keeps typing codes
/// that name nothing is guessing, or has the wrong screen, and either way the next attempt waits.
/// </para>
/// <para>
/// In memory: a restart forgets the misses, which hands a guesser one more window of
/// <see cref="PerSignIn"/> attempts against a space of about 2.6×10¹⁰ codes — nothing worth persisting.
/// The overall cap is what holds when the guesses are spread over many accounts.
/// </para>
/// </remarks>
internal sealed class DeviceCodeGuesses
{
    /// <summary>How long a miss is remembered.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Misses one browser sign-in may make inside the window.</summary>
    internal const int PerSignIn = 10;

    /// <summary>Misses the whole provider takes inside the window.</summary>
    internal const int Overall = 100;

    private const string Everyone = "";
    private readonly Dictionary<string, Queue<DateTimeOffset>> _misses = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>When <paramref name="signIn"/> may try again, or null when it may now.</summary>
    public DateTimeOffset? BlockedUntil(string signIn)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            DateTimeOffset? mine = Full(signIn, PerSignIn, now);
            DateTimeOffset? all = Full(Everyone, Overall, now);
            return mine is null ? all : all is null ? mine : mine > all ? mine : all;
        }
    }

    /// <summary>Record a code from <paramref name="signIn"/> that named no device.</summary>
    public void Missed(string signIn)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            Record(signIn, now);
            Record(Everyone, now);

            // A sign-in that stopped guessing leaves an empty queue behind; sweep them as misses arrive
            // rather than on a timer, since nothing else ever grows this.
            foreach (string key in _misses.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
                _misses.Remove(key);
        }
    }

    private DateTimeOffset? Full(string key, int limit, DateTimeOffset now)
    {
        if (!_misses.TryGetValue(key, out Queue<DateTimeOffset>? misses))
            return null;
        Prune(misses, now);
        return misses.Count >= limit ? misses.Peek() + Window : null;
    }

    private void Record(string key, DateTimeOffset now)
    {
        if (!_misses.TryGetValue(key, out Queue<DateTimeOffset>? misses))
            _misses[key] = misses = new Queue<DateTimeOffset>();
        Prune(misses, now);
        misses.Enqueue(now);
    }

    private static void Prune(Queue<DateTimeOffset> misses, DateTimeOffset now)
    {
        while (misses.Count > 0 && misses.Peek() + Window <= now)
            misses.Dequeue();
    }
}

/// <summary>
/// Where a person signs a device in: <c>/device</c>, the address a television shows beside its code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authenticated by the provider's own cookie.</b> Somebody not signed in here signs in first, as a
/// request in flight for the <see cref="DeviceClientId"/> pseudo-client, which is never registered and
/// never issued a code: completing it returns the browser here, carrying the code it arrived with.
/// </para>
/// <para>
/// <b>Nothing is approved without the person seeing what.</b> The code from a QR scan is already in the
/// address, so entering it proves nothing about intent; the page shows which application the device asks
/// for and what the device said it is, and the approval is a separate press (RFC 8628 §5.4). A decision
/// is a post that passes the same-origin gate every credential post here passes.
/// </para>
/// <para>
/// <b>Only an active account signs a device in.</b> The account is read again at every step, and again
/// when the device collects its session.
/// </para>
/// <para>
/// <b>Codes that name nothing are limited</b> (<see cref="DeviceCodeGuesses"/>), on every door that looks
/// one up.
/// </para>
/// </remarks>
internal static class DevicePageEndpoints
{
    /// <summary>
    /// The request-in-flight client the device page's own sign-in runs under. Never registered and never
    /// issued a code.
    /// </summary>
    internal const string DeviceClientId = "kgsm-device";

    /// <summary>What a person is told they are signing in to, on the device page's own sign-in.</summary>
    internal const string DeviceClientName = "sign in a device";

    private const int MaxFormBytes = 8 * 1024;

    /// <summary><c>GET /device</c>: the page, or its sign-in when this browser holds none.</summary>
    internal static async Task Page(HttpContext ctx)
    {
        if (!await RequireProviderAsync(ctx, json: false))
            return;

        string? code = DeviceAuthorization.Normalize(ctx.Request.Query["user_code"].ToString());
        if (await ctx.RequestServices.GetRequiredService<ProviderSessions>().CurrentAsync(ctx) is null)
        {
            AnchorOptions options = ctx.RequestServices.GetRequiredService<AnchorOptions>();
            AuthorizeRequest request = await OidcEndpoints.BeginRequestAsync(
                ctx, DeviceClientId, options.Issuer + Return(code), state: null, challenge: "", nonce: null, prompt: null);
            await OidcEndpoints.SignInPageAsync(ctx, request, message: null);
            return;
        }

        if (await ctx.RequestServices.GetRequiredService<ProviderBundle>()
                .TryServeAsync(ctx, ProviderBundle.Page.Device, clientOrigin: null, []))
            return;

        await ProviderPages.DeviceEntryAsync(ctx, code is null ? null : DeviceAuthorization.Format(code), message: null);
    }

    /// <summary>
    /// <c>GET /device/context</c>: who is signed in, and the device a <c>user_code</c> names.
    /// </summary>
    internal static async Task Context(HttpContext ctx)
    {
        if (await CallerAsync(ctx, json: true, write: false) is not var (session, user))
            return;

        DeviceContextRequest? device = null;
        if (ctx.Request.Query.ContainsKey("user_code"))
        {
            if (await LookUpAsync(ctx, session, ctx.Request.Query["user_code"].ToString(), json: true) is not { } found)
                return;
            device = found.Request;
        }

        await Endpoints.WriteJson(ctx, StatusCodes.Status200OK, new DeviceContext(
            new AuthorizeContextAccount(user.Username, user.DisplayName, UserStatuses.ToWire(user.Status)), device),
            AnchorJsonContext.Default.DeviceContext);
    }

    /// <summary>
    /// <c>GET /device/confirm</c>: the floor's confirmation, for a browser with no script — the device a
    /// code names, and the two buttons.
    /// </summary>
    internal static async Task Confirm(HttpContext ctx)
    {
        string typed = ctx.Request.Query["user_code"].ToString();
        if (await CallerAsync(ctx, json: false, write: false, returnCode: typed) is not var (session, user))
            return;

        if (await LookUpAsync(ctx, session, typed, json: false) is not { } found)
            return;

        await ProviderPages.DeviceConfirmAsync(ctx, found.Request.UserCode, found.Request.Application,
            found.Request.UserAgent, user.DisplayName);
    }

    /// <summary><c>POST /device/decision</c>: approve or refuse the device a code names.</summary>
    internal static async Task Decide(HttpContext ctx)
    {
        bool json = ctx.Request.HasJsonContentType();
        if (await CallerAsync(ctx, json, write: true) is not var (session, user))
            return;

        if (user.Status != UserStatus.Active)
        {
            await RefuseAsync(ctx, json, StatusCodes.Status403Forbidden, "account_pending",
                "Not approved yet", "This account has to be approved before it can sign a device in.");
            return;
        }

        string? typed;
        bool? approve;
        if (json)
        {
            DeviceDecisionRequest? body = await Endpoints.ReadBodyAsync(ctx, AnchorJsonContext.Default.DeviceDecisionRequest);
            (typed, approve) = (body?.UserCode, body?.Approve);
        }
        else if (ctx.Request.HasFormContentType && ctx.Request.ContentLength is null or <= MaxFormBytes)
        {
            IFormCollection form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            typed = form["user_code"].ToString();
            approve = form["decision"].ToString() switch { "approve" => true, "deny" => false, _ => null };
        }
        else
        {
            (typed, approve) = (null, null);
        }

        if (approve is not { } approved)
        {
            await RefuseAsync(ctx, json, StatusCodes.Status400BadRequest, "malformed_request",
                "Nothing decided", "Approve the device or refuse it.");
            return;
        }

        if (await LookUpAsync(ctx, session, typed, json) is not { } found)
            return;

        if (!await ctx.RequestServices.GetRequiredService<SqliteSessionRegistry>().DecideDeviceAsync(
                found.Hash, approved, session.Identity.ToJson(), user.UserId, ctx.RequestAborted))
        {
            await RefuseAsync(ctx, json, StatusCodes.Status404NotFound, "no_such_code",
                "Code not found", "That code is not waiting for approval any more. Start again on the device.");
            return;
        }

        if (json)
        {
            await Endpoints.WriteJson(ctx, StatusCodes.Status200OK,
                new DeviceDecisionResult(approved ? "approved" : "denied", found.Request.Application),
                AnchorJsonContext.Default.DeviceDecisionResult);
            return;
        }

        await ProviderPages.DeviceDecidedAsync(ctx, approved, found.Request.Application);
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    private sealed record Found(string Hash, DeviceContextRequest Request);

    /// <summary>
    /// The live, undecided device a typed code names, or null with the refusal written — counting a code
    /// that names nothing against this sign-in.
    /// </summary>
    private static async Task<Found?> LookUpAsync(HttpContext ctx, ProviderSessionRow session, string? typed, bool json)
    {
        // Without script a refusal is the entry form again, carrying why, so the person can type the code
        // again rather than being left on a page with nowhere to go.
        Task Refuse(int status, string code, string message) =>
            json
                ? Endpoints.Refuse(ctx, status, code, message)
                : ProviderPages.DeviceEntryAsync(ctx, typed, message, status);

        var guesses = ctx.RequestServices.GetRequiredService<DeviceCodeGuesses>();
        if (guesses.BlockedUntil(session.SessionId) is { } until)
        {
            ctx.Response.Headers.RetryAfter = Endpoints.RetryAfterSeconds(until, DateTimeOffset.UtcNow).ToString();
            await Refuse(StatusCodes.Status429TooManyRequests, "too_many_codes",
                "Too many codes that match no device. Try again shortly.");
            return null;
        }

        PendingDevice? pending = null;
        string? hash = null;
        if (DeviceAuthorization.Normalize(typed) is { } code)
        {
            hash = DeviceAuthorization.HashUserCode(code);
            pending = await ctx.RequestServices.GetRequiredService<SqliteSessionRegistry>()
                .FindPendingDeviceAsync(hash, ctx.RequestAborted);
        }

        RegisteredClient? client = pending is null ? null : ctx.RequestServices.GetRequiredService<ClientRegistry>().Find(pending.ClientId);
        Application? application = client is null ? null : ctx.RequestServices.GetRequiredService<ApplicationRegistry>().Of(client);
        if (pending is null || hash is null || application is null)
        {
            guesses.Missed(session.SessionId);
            await Refuse(StatusCodes.Status404NotFound, "no_such_code",
                "That code does not match a device waiting to be signed in. Check it against the screen.");
            return null;
        }

        return new Found(hash, new DeviceContextRequest(
            DeviceAuthorization.Format(DeviceAuthorization.Normalize(typed)!), application.Name, pending.UserAgent, pending.Expires));
    }

    /// <summary>
    /// The browser's provider session and the account it proves, or null with the refusal written.
    /// </summary>
    /// <remarks>
    /// The account is re-read, never taken from the session. A write needs the same-origin gate, checked
    /// before anything else about it is read. A page with no sign-in is sent to <c>/device</c>, which signs
    /// the person in and comes back with the code they arrived with.
    /// </remarks>
    private static async Task<(ProviderSessionRow Session, KgsmUser User)?> CallerAsync(
        HttpContext ctx, bool json, bool write, string? returnCode = null)
    {
        if (!await RequireProviderAsync(ctx, json))
            return null;

        if (write && !OidcEndpoints.IsSameOrigin(ctx))
        {
            await RefuseAsync(ctx, json, StatusCodes.Status403Forbidden, "cross_site",
                "Refused", "A device can only be signed in from this page.");
            return null;
        }

        if (await ctx.RequestServices.GetRequiredService<ProviderSessions>().CurrentAsync(ctx) is not { } session)
        {
            if (json)
                await Endpoints.Refuse(ctx, StatusCodes.Status401Unauthorized, "unauthenticated", "Sign in to continue.");
            else
                ctx.Response.Redirect(Return(DeviceAuthorization.Normalize(returnCode)));
            return null;
        }

        KgsmUser? user;
        try
        {
            user = await ctx.RequestServices.GetRequiredService<IUserStore>()
                .FindByCredentialAsync(session.Handle, ctx.RequestAborted);
        }
        catch (KgsmAuthProviderException)
        {
            await RefuseAsync(ctx, json, StatusCodes.Status503ServiceUnavailable, "authority_unavailable",
                "Unavailable", "The account store could not be read. Try again shortly.");
            return null;
        }

        if (user is null)
        {
            if (json)
                await Endpoints.Refuse(ctx, StatusCodes.Status401Unauthorized, "unauthenticated", "Sign in to continue.");
            else
                ctx.Response.Redirect(Return(DeviceAuthorization.Normalize(returnCode)));
            return null;
        }

        if (user.Status == UserStatus.Disabled)
        {
            await RefuseAsync(ctx, json, StatusCodes.Status403Forbidden, "account_disabled",
                "Account switched off", "This account has been switched off.");
            return null;
        }

        return (session, user);
    }

    /// <summary>Whether this provider signs anybody in here, with the refusal written when it does not.</summary>
    private static async Task<bool> RequireProviderAsync(HttpContext ctx, bool json)
    {
        AnchorOptions options = ctx.RequestServices.GetRequiredService<AnchorOptions>();
        if (options.IssuerUrl is not null && ctx.RequestServices.GetRequiredService<AnchorRole>().IsAuthority)
            return true;

        await RefuseAsync(ctx, json, StatusCodes.Status503ServiceUnavailable,
            options.IssuerUrl is null ? "no_issuer" : "not_authority",
            "Not signing in here", "This member does not sign anybody in.");
        return false;
    }

    /// <summary>The device page's address, carrying a code when there is one to carry.</summary>
    private static string Return(string? normalizedCode) =>
        normalizedCode is null
            ? DeviceAuthorization.VerificationPath
            : DeviceAuthorization.VerificationPath + "?user_code=" + DeviceAuthorization.Format(normalizedCode);

    private static Task RefuseAsync(HttpContext ctx, bool json, int status, string code, string title, string message) =>
        json
            ? Endpoints.Refuse(ctx, status, code, message)
            : ProviderPages.ProblemAsync(ctx, status, title, message);
}
