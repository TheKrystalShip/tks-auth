using System.Text;
using System.Text.Json;

using TheKrystalShip.Auth.Journal;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// Records what happened to the accounts as structured log lines: one line per fact that changed, never
/// one per request, and only when the action actually happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>The provider is the only witness.</b> It is where a person signs in, where an account is created,
/// and where authority is granted and taken away. Each line is <c>audit &lt;event&gt;</c> with the actor,
/// the origin and the payload as a JSON object, so <c>journalctl -u tks-auth</c> reads as the audit.
/// </para>
/// <para>
/// The names and the payloads come from <see cref="AuthEvents"/> and <see cref="AuthEventPayloads"/>, so a
/// field is spelled one way on every line of one type.
/// </para>
/// <para>
/// <b>Facts, not sentences.</b> No summary and no severity: wording is the reader's.
/// </para>
/// <para>
/// <b>Best-effort, always.</b> Nothing here throws. The action has already happened by the time one of
/// these is called, so refusing it because the record could not be written would trade a missing line for
/// a broken door.
/// </para>
/// </remarks>
internal sealed class AuditLog(ILogger<AuditLog> logger)
{
    /// <summary>A person at a browser.</summary>
    internal const string OriginUi = "ui";

    /// <summary>
    /// A person in Discord, reached through an application that exchanged a Discord credential here: an
    /// Activity presenting their token, or a bot acting for them.
    /// </summary>
    internal const string OriginDiscord = "discord";

    /// <summary>Record a session beginning or ending.</summary>
    /// <remarks>
    /// The actor is the identity that arrived, not this provider: nobody else was involved in a sign-in,
    /// and naming the component that minted the token would hide who came through the door.
    /// </remarks>
    internal Task SessionAsync(
        string type, string? userId, string username, string identity, string? provider,
        string? sid, string? userAgent, string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(
            type, actor, origin,
            AuthEventPayloads.Session(userId, username, identity, provider, sid, userAgent),
            ct);

    /// <summary>Record sessions being torn down before they expired.</summary>
    internal Task SessionRevokedAsync(
        string scope, string userId, string username, string? sid, int? count,
        string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(
            AuthEvents.SessionRevoked, actor, origin,
            AuthEventPayloads.SessionRevoked(scope, userId, username, sid, count), ct);

    /// <summary>Record a run of wrong passwords locking an account.</summary>
    /// <remarks>
    /// The actor is the account the attempts were made against, which is <em>not</em> a claim about
    /// who made them — nobody authenticated, so there is nobody to name. It is the subject, recorded
    /// so the row reads beside that account's other rows, and the reason this event exists at all.
    /// </remarks>
    internal Task LockedOutAsync(
        string userId, string username, string identity, int failedCount, DateTimeOffset until,
        string actor, CancellationToken ct = default) =>
        WriteAsync(
            AuthEvents.LockedOut, actor, origin: null,
            AuthEventPayloads.LockedOut(userId, username, identity, failedCount, until), ct);

    /// <summary>
    /// Record an account being provisioned, approved, disabled, deleted, or having its password changed.
    /// </summary>
    /// <remarks>
    /// Takes no password parameter and never will. What is recorded is that a credential was set and
    /// by whom — the only signal an account takeover leaves — and the credential is not part of that
    /// fact.
    /// </remarks>
    internal Task AccountAsync(
        string type, string userId, string username,
        string? fromStatus = null, string? toStatus = null, bool? byHolder = null,
        string actor = "", string? origin = null, CancellationToken ct = default) =>
        WriteAsync(
            type, actor, origin,
            AuthEventPayloads.Account(userId, username, fromStatus, toStatus, byHolder),
            ct);

    /// <summary>Record an external identity being attached to or detached from an account.</summary>
    internal Task IdentityAsync(
        string type, string userId, string username, string provider, string handle,
        string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(
            type, actor, origin, AuthEventPayloads.Identity(userId, username, provider, handle), ct);

    /// <summary>Record an account being given a role, or losing one.</summary>
    internal Task AssignmentAsync(
        string type, string assignmentId, string userId, string? username, string roleId, string? role,
        long authorityVersion, string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(
            type, actor, origin,
            AuthEventPayloads.Assignment(assignmentId, userId, username, roleId, role, authorityVersion), ct);

    /// <summary>Record the catalog of declared actions changing.</summary>
    internal Task CatalogAsync(
        string source, IReadOnlyList<string> added, IReadOnlyList<string> removed, long authorityVersion,
        string actor, CancellationToken ct = default) =>
        WriteAsync(AuthEvents.CatalogChanged, actor, origin: null,
            AuthEventPayloads.Catalog(source, added, removed, authorityVersion), ct);

    /// <summary>Record a permission or role changing or being removed.</summary>
    internal Task AuthorityRecordAsync(
        string type, string id, string? name, long authorityVersion, string actor, CancellationToken ct = default) =>
        WriteAsync(type, actor, origin: null, AuthEventPayloads.Authority(id, name, authorityVersion), ct);

    /// <summary>Record an application, or one of its clients, being registered, changed or removed.</summary>
    internal Task ApplicationAsync(
        string type, string id, string? name, string? clientId, string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(type, actor, origin, AuthEventPayloads.Application(id, name, clientId), ct);

    /// <summary>Record a token exchange given a token, or refused one for <paramref name="reason"/>.</summary>
    /// <remarks>
    /// The actor is the person the exchange is for, as far as it is known: whoever holds the Discord
    /// identity. The client that asked, and the one acting for them, ride in the payload.
    /// </remarks>
    internal Task TokenExchangeAsync(
        string client, string application, string? identity, string? userId, string? username,
        string? actedBy, string? reason, string actor, CancellationToken ct = default) =>
        WriteAsync(
            reason is null ? AuthEvents.TokenExchanged : AuthEvents.TokenExchangeRefused, actor, OriginDiscord,
            AuthEventPayloads.TokenExchange(client, application, identity, userId, username, actedBy, reason), ct);

    /// <summary>Record refusals at an administrator-only surface.</summary>
    /// <remarks>
    /// The actor is the account that was refused, when one was identified: the row reads beside that
    /// account's other rows, and says nothing about anybody when nobody was. <paramref name="count"/>
    /// is how many refusals the line stands for.
    /// </remarks>
    internal Task AdminRefusedAsync(
        string surface, string route, string? account, string? address, int count, CancellationToken ct = default) =>
        WriteAsync(
            AuthEvents.AdminRefused, account, OriginUi,
            AuthEventPayloads.AdminRefused(surface, route, account, address, count), ct);

    /// <summary>Write one line.</summary>
    private Task WriteAsync(
        string type, string? actor, string? origin, Action<Utf8JsonWriter> payload, CancellationToken ct)
    {
        try
        {
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();
                payload(writer);
                writer.WriteEndObject();
            }

            logger.LogInformation("audit {Event} actor={Actor} origin={Origin} {Payload}",
                type, actor ?? "unknown", origin ?? "unknown", Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "{Event} was not recorded: the action happened, the line did not", type);
        }

        return Task.CompletedTask;
    }
}
