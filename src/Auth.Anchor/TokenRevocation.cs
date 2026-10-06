using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Minting;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Anchor;

/// <summary>
/// Token revocation (RFC 7009): a client ends the session it holds by presenting that session's refresh
/// token — how a device signed in with the device grant signs out, having no <c>id_token</c> and no browser
/// sign-in for <c>/sign-out</c> to end.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refresh token ends its session, and nothing else.</b> The session is revoked the way every other
/// door ends one (<see cref="Endpoints.EndSessionAsync"/>), so the other members hear it, and the line
/// written is <c>auth.session.revoked</c> with scope <c>self</c>, its actor the identity the session
/// belongs to. A session minted under a browser's sign-in ends alone; the browser stays signed in, as RFC
/// 7009 revokes the grant the token belongs to and not the sign-in behind it. Any refresh token of the
/// session that verifies ends it, the current one or one already rotated away: whoever holds either held
/// the session, and a client whose last rotation was lost still signs out.
/// </para>
/// <para>
/// <b>Who may end it is who may continue it</b> (<see cref="Endpoints.CheckHolder"/>): the client the
/// session was minted for, authenticated when it is confidential, and for a KGSM session — recorded
/// without a client — whoever presents its refresh token. A confidential client's session presented
/// without its secret is <c>invalid_client</c>; one presented by, or naming, another client is
/// <c>unauthorized_client</c> and ends nothing.
/// </para>
/// <para>
/// <b>Not an oracle</b> (RFC 7009 §2.2). A token that does not verify, is expired, belongs to a session
/// already ended or to no session this provider continues answers <c>200</c> like one that was revoked.
/// An access token answers <c>200</c> and ends nothing: it is a bearer handed to resource servers, and any
/// of them could otherwise sign its holder out, while it lives minutes and dies with its session. The
/// hint is read by nobody, since a refresh token is recognised by what it is.
/// </para>
/// </remarks>
internal static class TokenRevocation
{
    /// <summary>The largest form this door reads. A revocation is a token and a client id.</summary>
    private const int MaxFormBytes = 8 * 1024;

    /// <summary><c>POST /revoke</c> (RFC 7009 §2.1).</summary>
    internal static async Task Revoke(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";

        IServiceProvider services = ctx.RequestServices;
        if (services.GetRequiredService<AnchorOptions>().IssuerUrl is null)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "This anchor has no issuer address, so it holds no sessions.");
            return;
        }

        if (!services.GetRequiredService<AnchorRole>().IsAuthority)
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
        var clients = services.GetRequiredService<ClientRegistry>();
        var auth = ClientAuthentication.Read(ctx.Request, form, clients);
        switch (auth.Result)
        {
            case ClientAuthentication.Outcome.Malformed:
                await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_request",
                    "A client authenticates one way: HTTP Basic, or client_id and client_secret in the form.");
                return;

            case ClientAuthentication.Outcome.Failed:
                await auth.RefuseAsync(ctx, "The client could not be authenticated.");
                return;

            // A public client names itself, and a name nobody registered is no client.
            case ClientAuthentication.Outcome.Unauthenticated
                when auth.NamedClientId is { } named && clients.Find(named) is null:
                await auth.RefuseAsync(ctx, "The client could not be authenticated.");
                return;
        }

        if (OidcEndpoints.Single(form, "token") is not { } presented)
        {
            await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_request",
                "token is required.");
            return;
        }

        RefreshClaims? claims = await services.GetRequiredService<ISessionTokenService>().ReadRefreshAsync(presented);
        if (claims is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        switch (Endpoints.CheckHolder(ctx, claims, auth, out _, out _))
        {
            case Endpoints.HolderCheck.ClientUnauthenticated:
                await auth.RefuseAsync(ctx, "This client authenticates with its secret.");
                return;

            case Endpoints.HolderCheck.OtherClient:
                await OidcEndpoints.OAuthErrorAsync(ctx, StatusCodes.Status400BadRequest, "unauthorized_client",
                    "That token was not issued to this client.");
                return;

            case Endpoints.HolderCheck.NotHeld:
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return;
        }

        if (await Endpoints.EndSessionAsync(ctx, claims.SessionId))
            await RecordAsync(ctx, claims);

        ctx.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>
    /// Record the session's holder ending it. The account is looked up for the line alone: the session is
    /// already ended, and a store that cannot be read leaves the line naming the identity without the id.
    /// </summary>
    private static async Task RecordAsync(HttpContext ctx, RefreshClaims claims)
    {
        KgsmUser? user;
        try
        {
            user = await ctx.RequestServices.GetRequiredService<AccountResolver>()
                .FindAsync(claims.Identity, ctx.RequestAborted);
        }
        catch (KgsmAuthProviderException)
        {
            user = null;
        }

        await ctx.RequestServices.GetRequiredService<AnchorJournal>().SessionRevokedAsync(
            SessionRevokeScopes.Self,
            userId: user?.UserId ?? string.Empty,
            username: user?.Username ?? claims.Identity.Username,
            sid: claims.SessionId,
            count: null,
            actor: claims.Identity.ActorString,
            origin: AnchorJournal.OriginUi,
            ct: ctx.RequestAborted);
    }
}
