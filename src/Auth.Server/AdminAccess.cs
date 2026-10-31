using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server;

/// <summary>The provider's answer to one of its own actions: allowed, refused, or allowed once the person proves themselves again.</summary>
/// <param name="Decision">The evaluator's answer.</param>
/// <param name="ReauthRequired">
/// Allowed by the evaluator, and an <c>auth:*</c> action whose session has not proved a credential within
/// the re-authentication window. Answered with <c>reauth_required</c>, so the page sends the person to
/// prove one and brings them back.
/// </param>
internal readonly record struct AdminAccessResult(AccessDecision Decision, bool ReauthRequired)
{
    public bool Allowed => Decision.Allowed && !ReauthRequired;
}

/// <summary>
/// Decides the provider's own actions for a signed-in person: the evaluator first, then, for every
/// <c>auth:*</c> action, a recent sign-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The provider serves every <c>auth:*</c> action itself and reads its own session registry</b>, so no
/// token carries when somebody last proved a credential. The window is the one changing one's own
/// sign-in methods already requires.
/// </para>
/// <para>
/// The evaluator is asked first. A person who may not do something is told so, not sent to type their
/// password for an action that would be refused anyway.
/// </para>
/// </remarks>
internal sealed class AdminAccess(
    ServerAuthority authority,
    SqliteSessionRegistry sessions,
    ServerOptions options,
    TimeProvider clock)
{
    /// <summary>Whether <paramref name="accountId"/>, holding session <paramref name="sessionId"/>, may perform <paramref name="action"/>.</summary>
    /// <exception cref="InvalidOperationException">The store holds no authority.</exception>
    public async Task<AdminAccessResult> AllowsAsync(
        string accountId, string sessionId, string action, CancellationToken ct = default)
    {
        AuthoritySource source = authority.Source
            ?? throw new InvalidOperationException(authority.UnavailableReason ?? "The account store holds no authority.");

        AccessEvaluator evaluator = new(await source.CurrentAsync(ct).ConfigureAwait(false));
        AccessDecision decision = evaluator.Allows(accountId, action);
        if (!decision.Allowed || !ActionIds.IsAuth(action))
            return new AdminAccessResult(decision, ReauthRequired: false);

        DateTimeOffset? proved = await sessions.CredentialAtAsync(sessionId, ct).ConfigureAwait(false);
        bool recent = proved is { } at && clock.GetUtcNow() - at <= options.ReauthWindow;
        return new AdminAccessResult(decision, ReauthRequired: !recent);
    }
}
