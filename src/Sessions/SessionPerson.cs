using System.Security.Claims;
using System.Text.Json;

namespace TheKrystalShip.Auth.Sessions;

/// <summary>
/// The person a request acts as, read from the access token the provider issued: who they are, what they
/// are called, the actions they hold, when the token stops being valid, and the client acting for them
/// when one is.
/// </summary>
/// <param name="Subject">The token's <c>sub</c>: the account, which every record about a person is keyed by.</param>
/// <param name="DisplayName">The token's <c>name</c>, else its <c>preferred_username</c>, else the subject.</param>
/// <param name="Actions">The action ids the token lists.</param>
/// <param name="ExpiresAt">The token's <c>exp</c>.</param>
/// <param name="Via">The <c>act</c> claim's <c>sub</c> (RFC 8693 §4.1): the client calling on the person's behalf.</param>
public sealed record SessionPerson(
    string Subject, string DisplayName, IReadOnlySet<string> Actions, DateTimeOffset ExpiresAt, string? Via = null)
{
    private const string ItemKey = "tks-session-person";

    /// <summary>Whether the token lists <paramref name="action"/>.</summary>
    public bool Holds(string action) => Actions.Contains(action);

    /// <summary>The person the request's token names, or null when it carries none.</summary>
    public static SessionPerson? Of(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var person) ? person as SessionPerson : null;

    /// <summary>
    /// A token source cancelled when the request aborts or the person's token expires, whichever comes
    /// first. A stream that runs on it ends with the token, and the client reconnects with a fresh one.
    /// A request carrying no person is cancelled with the request alone.
    /// </summary>
    public static CancellationTokenSource UntilExpiry(HttpContext context, TimeProvider? time = null)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (Of(context) is { } person)
        {
            var left = person.ExpiresAt - (time ?? TimeProvider.System).GetUtcNow();
            if (left <= TimeSpan.Zero) source.Cancel();
            else source.CancelAfter(left);
        }
        return source;
    }

    internal void AttachTo(HttpContext context) => context.Items[ItemKey] = this;

    /// <summary>
    /// Reads the person off a validated token, or null when it names nobody. With <paramref name="declared"/>,
    /// the person holds only the actions among it.
    /// </summary>
    public static SessionPerson? From(ClaimsIdentity identity, string actionsClaim, IReadOnlySet<string>? declared = null)
    {
        if (identity.FindFirst("sub")?.Value is not { Length: > 0 } sub) return null;
        if (!long.TryParse(identity.FindFirst("exp")?.Value, out var exp)) return null;

        var name = identity.FindFirst("name")?.Value is { Length: > 0 } shown ? shown
            : identity.FindFirst("preferred_username")?.Value is { Length: > 0 } username ? username
            : sub;
        var actions = identity.FindAll(actionsClaim)
            .Select(c => c.Value)
            .Where(a => declared is null || declared.Contains(a))
            .ToHashSet(StringComparer.Ordinal);

        return new SessionPerson(sub, name, actions, DateTimeOffset.FromUnixTimeSeconds(exp), ActorOf(identity.FindFirst("act")?.Value));
    }

    /// <summary>The acting client's id out of an <c>act</c> claim, an object the token handler carries as JSON.</summary>
    private static string? ActorOf(string? act)
    {
        if (string.IsNullOrEmpty(act)) return null;
        try
        {
            return JsonSerializer.Deserialize(act, SessionsJsonContext.Default.ActorClaim)?.Sub is { Length: > 0 } sub ? sub : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
