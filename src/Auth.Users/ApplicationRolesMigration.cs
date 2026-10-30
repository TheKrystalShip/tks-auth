using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Users;

/// <summary>A role the migration leaves as it is, and how many cluster-wide holders it has.</summary>
/// <param name="RoleId">The role.</param>
/// <param name="Name">Its name.</param>
/// <param name="Holders">The accounts holding it at cluster scope.</param>
public sealed record CarriedRole(string RoleId, string Name, int Holders);

/// <summary>An account that is to hold the administrator role.</summary>
/// <param name="AccountId">The account.</param>
/// <param name="Name">Its username.</param>
public sealed record AdministratorGrant(string AccountId, string Name);

/// <summary>An assignment the migration removes: one held at a node or an instance.</summary>
/// <param name="AssignmentId">The assignment.</param>
/// <param name="AccountId">Who holds it.</param>
/// <param name="Account">The holder's username.</param>
/// <param name="Role">The role's name.</param>
/// <param name="Scope">Where it is held.</param>
public sealed record DroppedAssignment(string AssignmentId, string AccountId, string Account, string Role, string Scope);

/// <summary>What <see cref="ApplicationRolesMigration.Plan"/> found, and what applying it does.</summary>
/// <param name="Application">The application the authority moves under.</param>
/// <param name="AdministratorName">The administrator role's name.</param>
/// <param name="AdministratorRoleId">The existing role of that name, or null when the migration creates it.</param>
/// <param name="FlagsAdministrator">Whether the role is yet to be made to hold every action of the application.</param>
/// <param name="Administrators">The Owners who are yet to be given the administrator role.</param>
/// <param name="Carried">The custom roles kept as they are.</param>
/// <param name="Dropped">The assignments removed.</param>
public sealed record ApplicationRolesPlan(
    string Application,
    string AdministratorName,
    string? AdministratorRoleId,
    bool FlagsAdministrator,
    IReadOnlyList<AdministratorGrant> Administrators,
    IReadOnlyList<CarriedRole> Carried,
    IReadOnlyList<DroppedAssignment> Dropped)
{
    /// <summary>Whether applying the plan writes nothing.</summary>
    public bool IsEmpty =>
        AdministratorRoleId is not null && !FlagsAdministrator && Administrators.Count == 0 && Dropped.Count == 0;
}

/// <summary>
/// Moves an authority whose grants are scoped (cluster, node, instance) to an application's: a role
/// with an account at the whole organization's scope and nothing narrower.
/// </summary>
/// <remarks>
/// <para>
/// Roles, permissions and assignments live in one store beside the accounts, and a role belongs to no
/// application: an application's roles are the roles whose permissions file its actions, and a token for
/// it lists the actions the account holds at cluster scope. A custom role and its cluster-wide holders
/// therefore need no change. What changes is the Owner's reach into the application, which becomes a role
/// that holds every action of it, and the grants an application has no scope for, which are removed.
/// </para>
/// <para>
/// The plan is read from a snapshot and applied by <see cref="SqliteAuthorityStore.MigrateToApplicationRolesAsync"/>
/// from the snapshot of its own transaction, so a plan shown and a plan applied cannot differ by a write
/// in between. Applying one a second time finds nothing to do.
/// </para>
/// </remarks>
public static class ApplicationRolesMigration
{
    /// <summary>What migrating <paramref name="snapshot"/> to <paramref name="application"/>'s roles does.</summary>
    /// <exception cref="InvalidOperationException">The administrator role's name is a built-in role's.</exception>
    public static ApplicationRolesPlan Plan(AuthoritySnapshot snapshot, string application, string administratorName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string key = AuthorityRules.NameKey(administratorName);
        Role? existing = snapshot.Roles.Values.FirstOrDefault(r => AuthorityRules.NameKey(r.Name) == key);
        if (existing is { Kind: not RoleKind.Custom })
            throw new InvalidOperationException($"'{existing.Name}' is a built-in role; the administrator role needs a name of its own.");

        string? roleId = existing?.RoleId;

        List<AdministratorGrant> administrators = [];
        foreach (string accountId in snapshot.Assignments
                     .Where(a => a.RoleId == BuiltInRoles.OwnerId && a.Scope.Kind == ScopeKind.Cluster)
                     .Select(a => a.AccountId).Distinct(StringComparer.Ordinal))
        {
            if (!snapshot.Accounts.TryGetValue(accountId, out AccessAccount? account) || account.Kind != AccountKind.Person)
                continue;

            bool holds = roleId is not null && snapshot.AssignmentsOf(accountId)
                .Any(a => a.RoleId == roleId && a.Scope.Kind == ScopeKind.Cluster);
            if (!holds)
                administrators.Add(new AdministratorGrant(accountId, account.Name));
        }

        CarriedRole[] carried =
        [
            .. snapshot.Roles.Values
                .Where(r => r.Kind == RoleKind.Custom && r.RoleId != roleId)
                .OrderBy(r => r.Rank)
                .Select(r => new CarriedRole(r.RoleId, r.Name,
                    snapshot.Assignments.Count(a => a.RoleId == r.RoleId && a.Scope.Kind == ScopeKind.Cluster))),
        ];

        DroppedAssignment[] dropped =
        [
            .. snapshot.Assignments
                .Where(a => a.Scope.Kind != ScopeKind.Cluster)
                .Select(a => new DroppedAssignment(
                    a.AssignmentId,
                    a.AccountId,
                    snapshot.Accounts.TryGetValue(a.AccountId, out AccessAccount? holder) ? holder.Name : a.AccountId,
                    snapshot.Roles.TryGetValue(a.RoleId, out Role? role) ? role.Name : a.RoleId,
                    a.Scope.ToString()))
                .OrderBy(d => d.Account, StringComparer.Ordinal).ThenBy(d => d.Role, StringComparer.Ordinal)
                .ThenBy(d => d.Scope, StringComparer.Ordinal),
        ];

        return new ApplicationRolesPlan(
            application, administratorName.Trim(), roleId,
            FlagsAdministrator: existing is null || existing.HoldsEveryActionOf != application,
            administrators, carried, dropped);
    }
}
