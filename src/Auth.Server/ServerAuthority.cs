using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// The provider's authority store, opened on first use.
/// </summary>
/// <remarks>
/// The authority lives in the account store. A store at a schema version this build does not read
/// says so once through <see cref="UnavailableReason"/> rather than stopping the daemon, which is the
/// surface that reports it.
/// </remarks>
internal sealed class ServerAuthority(ServerOptions options, ILogger<ServerAuthority> logger)
{
    private readonly Lock _gate = new();
    private SqliteAuthorityStore? _store;
    private AuthoritySource? _source;
    private string? _unavailable;

    /// <summary>The snapshot the provider evaluates its own actions against, cached per change.</summary>
    public AuthoritySource? Source
    {
        get
        {
            if (Store is not { } store)
                return null;

            lock (_gate)
                return _source ??= new AuthoritySource(store);
        }
    }

    /// <summary>Why there is no store, when there is none.</summary>
    public string? UnavailableReason => _unavailable;

    /// <summary>The store, or <see langword="null"/> when the file cannot hold the authority.</summary>
    public SqliteAuthorityStore? Store
    {
        get
        {
            lock (_gate)
            {
                if (_store is not null)
                    return _store;

                try
                {
                    _store = new SqliteAuthorityStore(new UserStoreOptions { Path = options.UserStorePath });
                    _unavailable = null;
                }
                catch (UserStoreSchemaException e)
                {
                    if (_unavailable is null)
                        logger.LogWarning("the authority store is unavailable: {Reason}", e.Message);

                    _unavailable = e.Message;
                }

                return _store;
            }
        }
    }
}

/// <summary>
/// Where every report of actions reaches the store — an application's, the provider's own — and the one
/// place each write it makes is journaled.
/// </summary>
internal sealed class AuthorityIntake(
    ServerAuthority authority,
    AuditLog journal,
    ILogger<AuthorityIntake> logger)
{
    /// <summary>Record what <paramref name="source"/> declares.</summary>
    public async Task DeclaredAsync(string source, CatalogReport report, CancellationToken ct)
    {
        if (authority.Store is not { } store)
        {
            logger.LogWarning("'{Source}' declared its actions and there is no authority store to hold them", source);
            return;
        }

        AuthorityWrite write = await store.RecordReportAsync(source, report, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await SettleAsync(source, write, ct).ConfigureAwait(false);
    }

    /// <summary>A removed source: forget everything it declared.</summary>
    public async Task ForgetAsync(string source, CancellationToken ct)
    {
        if (authority.Store is not { } store)
            return;

        AuthorityWrite write = await store.ForgetReportAsync(source, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await SettleAsync(source, write, ct).ConfigureAwait(false);
    }

    /// <summary>Journal a write.</summary>
    private async Task SettleAsync(string source, AuthorityWrite write, CancellationToken ct)
    {
        string[] added = [.. write.Changes.Where(c => c.Kind == AuthorityChangeKind.CatalogActionAdded).Select(c => c.Subject)];
        if (added.Length > 0)
            logger.LogInformation("'{Source}' declared {Count} action(s) new to the catalog, unmapped: {Actions}",
                source, added.Length, string.Join(", ", added));

        await AuthorityJournaling.JournalAsync(
            journal, write, ActorStrings.Format(ActorProvider.System, source), origin: null, source, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// One journal line per change an authority write made, whoever made it.
/// </summary>
/// <remarks>
/// A cascade is journaled as the changes it made — deleting a role is the role's line and one line per
/// assignment that ended with it — so an access review reads what happened to each person rather than
/// inferring it. The catalog's arrivals and departures from one report are one line.
/// </remarks>
internal static class AuthorityJournaling
{
    /// <param name="journal">Where the lines go.</param>
    /// <param name="write">What the write changed.</param>
    /// <param name="actor">Who made it: a person, or <c>system:&lt;source&gt;</c> for a report.</param>
    /// <param name="origin">The surface a person made it from, when a person did.</param>
    /// <param name="source">The source whose report changed the catalog, when one did.</param>
    /// <param name="ct">Cancellation.</param>
    internal static async Task JournalAsync(
        AuditLog journal, AuthorityWrite write, string actor, string? origin, string? source, CancellationToken ct)
    {
        if (write.Changes.Count == 0)
            return;

        string[] added = [.. write.Changes.Where(c => c.Kind == AuthorityChangeKind.CatalogActionAdded).Select(c => c.Subject)];
        string[] removed = [.. write.Changes.Where(c => c.Kind == AuthorityChangeKind.CatalogActionRemoved).Select(c => c.Subject)];
        if (added.Length > 0 || removed.Length > 0)
            await journal.CatalogAsync(source ?? "", added, removed, write.Version, actor, ct).ConfigureAwait(false);

        foreach (AuthorityChange change in write.Changes)
        {
            switch (change.Kind)
            {
                case AuthorityChangeKind.RoleChanged:
                case AuthorityChangeKind.RoleRemoved:
                case AuthorityChangeKind.PermissionChanged:
                case AuthorityChangeKind.PermissionRemoved:
                    await journal.AuthorityRecordAsync(RecordEvent(change.Kind), change.Subject, change.Name,
                        write.Version, actor, ct).ConfigureAwait(false);
                    break;

                case AuthorityChangeKind.AssignmentGranted:
                case AuthorityChangeKind.AssignmentRevoked:
                    await journal.AssignmentAsync(
                        change.Kind == AuthorityChangeKind.AssignmentGranted ? AuthEvents.AssignmentGranted : AuthEvents.AssignmentRevoked,
                        change.Subject, change.AccountId!, null, change.RoleId!, change.Name,
                        write.Version, actor, origin, ct).ConfigureAwait(false);
                    break;

                case AuthorityChangeKind.AccountDisabled:
                    await journal.AccountAsync(AuthEvents.UserDisabled, change.Subject, change.Name ?? change.Subject,
                        fromStatus: UserStatuses.Active, toStatus: UserStatuses.Disabled, actor: actor, origin: origin, ct: ct)
                        .ConfigureAwait(false);
                    break;

                case AuthorityChangeKind.AccountDeleted:
                    await journal.AccountAsync(AuthEvents.UserDeleted, change.Subject, change.Name ?? change.Subject,
                        actor: actor, origin: origin, ct: ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static string RecordEvent(AuthorityChangeKind kind) => kind switch
    {
        AuthorityChangeKind.RoleChanged => AuthEvents.RoleChanged,
        AuthorityChangeKind.RoleRemoved => AuthEvents.RoleRemoved,
        AuthorityChangeKind.PermissionChanged => AuthEvents.PermissionChanged,
        _ => AuthEvents.PermissionRemoved,
    };
}
