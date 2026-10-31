using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// Somebody's sessions, read and ended on <c>auth:accounts.disable</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>They are listed here because they exist only here.</b> A session has a row on the provider and
/// nowhere else; the applications verify its tokens offline and store nothing.
/// </para>
/// <para>
/// A person's own sessions are the account page's (<see cref="AccountPageEndpoints"/>), authenticated
/// by the provider's cookie. These doors act on somebody else's, reached from the admin pages with the
/// bearer they hold, and each addresses the account it acts on.
/// </para>
/// <para>
/// <b>Ending a session is gated on <c>auth:accounts.disable</c> and nothing else.</b> Revoking takes
/// authority away, so it is never held to a recent sign-in.
/// </para>
/// </remarks>
internal static class SessionEndpoints
{
    /// <summary>Every live session an account holds.</summary>
    internal static async Task List(HttpContext ctx)
    {
        Caller? maybe = await Endpoints.RequireCaller(ctx, AuthAction.Of(ctx));
        if (maybe is not { } caller)
            return;

        if (ctx.Request.RouteValues["userId"] as string is not { Length: > 0 } userId)
        {
            await Endpoints.Refuse(ctx, StatusCodes.Status400BadRequest, "invalid_user",
                "A user id is required.");
            return;
        }

        if (await ctx.RequestServices.GetRequiredService<IUserStore>()
                .FindByIdAsync(userId, ctx.RequestAborted) is not { } subject)
        {
            await Endpoints.Refuse(ctx, StatusCodes.Status404NotFound, "no_such_account",
                "No account has that id.");
            return;
        }

        IReadOnlyList<SqliteSessionRegistry.LiveSession> live =
            await RegistryOf(ctx).ListAsync(await HandlesOf(ctx, subject), ctx.RequestAborted);

        await Endpoints.WriteJson(ctx, StatusCodes.Status200OK, new SessionsPage(
            [.. live.Select(s => new SessionRecord(
                s.SessionId, s.UserId, s.Created, s.Expires, s.UserAgent, s.LastSeen,
                // True on exactly the row the calling bearer belongs to, so somebody reading
                // their own account here sees which device is the one they are using.
                Current: string.Equals(s.SessionId, caller.SessionId, StringComparison.Ordinal),
                Kind: s.Kind,
                Name: s.Name))]),
            ServerJsonContext.Default.SessionsPage);
    }

    /// <summary>End every session an account holds.</summary>
    /// <remarks>
    /// The one a person cannot do for themselves and the one an incident needs: somebody's access is
    /// withdrawn and every device they are signed in on has to stop, without waiting for a bearer to
    /// expire on each.
    /// </remarks>
    internal static async Task RevokeAll(HttpContext ctx)
    {
        Caller? maybe = await Endpoints.RequireCaller(ctx, AuthAction.Of(ctx));
        if (maybe is not { } caller)
            return;

        if (ctx.Request.RouteValues["userId"] as string is not { Length: > 0 } userId)
        {
            await Endpoints.Refuse(ctx, StatusCodes.Status400BadRequest, "invalid_user",
                "A user id is required.");
            return;
        }

        if (await ctx.RequestServices.GetRequiredService<IUserStore>()
                .FindByIdAsync(userId, ctx.RequestAborted) is not { } subject)
        {
            await Endpoints.Refuse(ctx, StatusCodes.Status404NotFound, "no_such_account",
                "No account has that id.");
            return;
        }

        await EndAllAsync(
            ctx, subject, await HandlesOf(ctx, subject), SessionRevokeScopes.Other, caller);
    }

    /// <summary>End one of somebody else's sessions.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately not the same fact as ending all of them. "This one session looks wrong" and "sign
    /// this person out everywhere" are different decisions with different costs: the first ends a
    /// device without disturbing somebody mid-task, and somebody left only the second reaches for it
    /// because it is what exists.
    /// </para>
    /// <para>
    /// The session is addressed <em>under the account it belongs to</em>, so the check is whether this
    /// sid is that person's rather than whether it exists. Ending a session without knowing
    /// whose it was could not be recorded honestly, and the row has to name the subject.
    /// </para>
    /// </remarks>
    internal static async Task RevokeOne(HttpContext ctx)
    {
        Caller? maybe = await Endpoints.RequireCaller(ctx, AuthAction.Of(ctx));
        if (maybe is not { } caller)
            return;

        if (ctx.Request.RouteValues["userId"] as string is not { Length: > 0 } userId
            || ctx.Request.RouteValues["sid"] as string is not { Length: > 0 } sid)
        {
            await Endpoints.Refuse(ctx, StatusCodes.Status400BadRequest, "invalid_session",
                "A user id and a session id are required.");
            return;
        }

        if (await ctx.RequestServices.GetRequiredService<IUserStore>()
                .FindByIdAsync(userId, ctx.RequestAborted) is not { } subject)
        {
            await Endpoints.Refuse(ctx, StatusCodes.Status404NotFound, "no_such_account",
                "No account has that id.");
            return;
        }

        IReadOnlyList<string> handles = await HandlesOf(ctx, subject);

        // A sid that belongs to somebody else answers the same as one that does not exist: telling
        // those apart says whether an id is real, and somebody acting on the wrong account should be
        // told they have the wrong account rather than shown a stranger's session.
        if (await RegistryOf(ctx).OwnerAsync(sid, ctx.RequestAborted) is not { } owner
            || !handles.Contains(owner, StringComparer.Ordinal))
        {
            await Endpoints.Refuse(ctx, StatusCodes.Status404NotFound, "no_such_session",
                "That account holds no session with that id.");
            return;
        }

        await EndAsync(ctx, sid);
        await RecordAsync(ctx, SessionRevokeScopes.Other, subject, sid, 1, caller);

        await Endpoints.WriteJson(ctx, StatusCodes.Status200OK, new RevokeResult(1),
            ServerJsonContext.Default.RevokeResult);
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    private static async Task EndAllAsync(
        HttpContext ctx, UserAccount subject, IReadOnlyList<string> handles, string scope, Caller caller)
    {
        IReadOnlyList<string> ended =
            await RegistryOf(ctx).RevokeAllAsync(handles, ctx.RequestAborted);

        var validator = ctx.RequestServices.GetRequiredService<ISessionValidator>();

        foreach (string sid in ended)
        {
            validator.Evict(sid);
        }

        // Zero is a real answer and not a failure: an account with nothing live is exactly what was
        // wanted, and reporting it as an error would invite them to try again.
        if (ended.Count > 0)
            await RecordAsync(ctx, scope, subject, sid: null, ended.Count, caller);

        await Endpoints.WriteJson(ctx, StatusCodes.Status200OK, new RevokeResult(ended.Count),
            ServerJsonContext.Default.RevokeResult);
    }

    private static async Task EndAsync(HttpContext ctx, string sid)
    {
        // A browser's sign-in at the provider is ended as signing out is: with every session minted under
        // it, or the next bounce from any of those surfaces would find them still live.
        if (await RegistryOf(ctx).IsProviderSessionAsync(sid, ctx.RequestAborted))
        {
            await ctx.RequestServices.GetRequiredService<ProviderSessions>().EndAsync(ctx, sid);
            return;
        }

        await Endpoints.EndSessionAsync(ctx, sid);
    }

    /// <summary>
    /// Record what was ended.
    /// </summary>
    /// <remarks>
    /// A single revocation names the session; a sweep names how many it ended. Neither fabricates the
    /// other, which is why both ride as nullable rather than being defaulted to each other.
    /// </remarks>
    private static Task RecordAsync(
        HttpContext ctx, string scope, UserAccount subject, string? sid, int? count, Caller caller) =>
        ctx.RequestServices.GetRequiredService<AuditLog>().SessionRevokedAsync(
            scope, subject.UserId, subject.Username, sid, count,
            // Whoever acted. Ending somebody else's sessions is the substantial-power case,
            // and the account acted upon is in the payload rather than in the actor.
            actor: caller.Identity?.ActorString
                ?? (caller.User is { } self ? self.AsIdentity().ActorString : string.Empty),
            origin: AuditLog.OriginUi,
            ct: ctx.RequestAborted);

    private static SqliteSessionRegistry RegistryOf(HttpContext ctx) =>
        (SqliteSessionRegistry)ctx.RequestServices.GetRequiredService<ISessionRegistry>();

    /// <summary>
    /// Every handle an account's sessions could be keyed by.
    /// </summary>
    /// <remarks>
    /// Never the user id and never the username. A session is keyed by the provider-qualified handle
    /// somebody <em>arrived</em> with, so one account signed in with a password and with Discord has
    /// two keys — asking under one finds half the devices and reports the other half as nothing,
    /// which reads as an empty card rather than as a wrong question. Every credential the account
    /// holds is checked, which is what makes "sign me out everywhere" mean everywhere rather than
    /// everywhere-I-used-this-door.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> HandlesOf(HttpContext ctx, UserAccount user)
    {
        IReadOnlyList<UserCredential> credentials = await ctx.RequestServices
            .GetRequiredService<IUserStore>()
            .ListCredentialsAsync(user.UserId, ctx.RequestAborted);

        // The password handle is included by the credential list itself (a password is filed under
        // local:<user id>), so nothing is added here that the store did not say the account holds.
        return [.. credentials.Select(c => c.Handle).Distinct(StringComparer.Ordinal)];
    }
}
