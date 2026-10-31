using System.Collections.Frozen;

namespace TheKrystalShip.Auth.Access;

/// <summary>
/// Everything that decides access, at one authority version: accounts, the catalog, permissions,
/// roles and assignments.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, so one snapshot is shared by every concurrent evaluation and a new version is a new
/// snapshot rather than an edit under a reader's feet. The indexes an evaluation walks are built once,
/// here.
/// </para>
/// <para>
/// The store builds one. Nothing here reads a file: that is the store's job.
/// </para>
/// </remarks>
public sealed class AuthoritySnapshot
{
    private static readonly IReadOnlyList<Assignment> NoAssignments = [];
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NoNamespaces =
        new Dictionary<string, IReadOnlySet<string>>();

    private readonly FrozenDictionary<string, IReadOnlyList<Assignment>> _assignmentsByAccount;
    private readonly FrozenDictionary<string, FrozenSet<string>> _roleActions;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _applicationNamespaces;

    /// <summary>Build a snapshot, and the indexes an evaluation walks.</summary>
    public AuthoritySnapshot(
        long version,
        IEnumerable<AccessAccount> accounts,
        IEnumerable<CatalogAction> catalog,
        IEnumerable<Permission> permissions,
        IEnumerable<Role> roles,
        IEnumerable<Assignment> assignments,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? applicationNamespaces = null)
    {
        _applicationNamespaces = applicationNamespaces ?? NoNamespaces;
        Version = version;

        Accounts = accounts.ToFrozenDictionary(a => a.AccountId, StringComparer.Ordinal);
        Catalog = catalog.ToFrozenDictionary(a => a.Id, StringComparer.Ordinal);
        Permissions = permissions.ToFrozenDictionary(p => p.PermissionId, StringComparer.Ordinal);
        Roles = roles.ToFrozenDictionary(r => r.RoleId, StringComparer.Ordinal);

        Assignment[] allAssignments = [.. assignments];
        Assignments = allAssignments;
        _assignmentsByAccount = allAssignments
            .GroupBy(a => a.AccountId, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<Assignment>)[.. g], StringComparer.Ordinal);

        _roleActions = Roles.Values.ToFrozenDictionary(
            r => r.RoleId,
            r => r.Permissions
                .Where(Permissions.ContainsKey)
                .SelectMany(p => Permissions[p].Actions)
                .Concat(r.HoldsEveryActionOf is { } application ? ActionsOfApplication(application) : [])
                .ToFrozenSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The namespaces <paramref name="application"/> owns: its id, and each component its manifests declare.
    /// </summary>
    public IReadOnlySet<string> NamespacesOf(string application)
    {
        HashSet<string> owned = new(StringComparer.Ordinal) { application };
        if (_applicationNamespaces.TryGetValue(application, out IReadOnlySet<string>? declared))
            owned.UnionWith(declared);

        return owned;
    }

    /// <summary>
    /// The catalog's actions in the namespaces <paramref name="application"/> owns: what a token for it can
    /// list, and what a role that holds every action of it holds.
    /// </summary>
    public IReadOnlyList<string> ActionsOfApplication(string application)
    {
        IReadOnlySet<string> owned = NamespacesOf(application);
        return [.. Catalog.Keys.Where(id => ActionIds.TryParse(id, out string component, out _) && owned.Contains(component))];
    }

    /// <summary>The authority version this snapshot was read at.</summary>
    public long Version { get; }

    /// <summary>Every account, by id.</summary>
    public IReadOnlyDictionary<string, AccessAccount> Accounts { get; }

    /// <summary>Every declared action, by id.</summary>
    public IReadOnlyDictionary<string, CatalogAction> Catalog { get; }

    /// <summary>Every permission, by id.</summary>
    public IReadOnlyDictionary<string, Permission> Permissions { get; }

    /// <summary>Every role, by id, the built-ins included.</summary>
    public IReadOnlyDictionary<string, Role> Roles { get; }

    /// <summary>Every assignment.</summary>
    public IReadOnlyList<Assignment> Assignments { get; }

    /// <summary>The assignments an account holds.</summary>
    public IReadOnlyList<Assignment> AssignmentsOf(string accountId) =>
        _assignmentsByAccount.GetValueOrDefault(accountId, NoAssignments);

    /// <summary>The actions a role grants through its permissions. Owner's are implicit and not listed.</summary>
    public IReadOnlySet<string> ActionsOf(string roleId) =>
        _roleActions.TryGetValue(roleId, out FrozenSet<string>? actions) ? actions : FrozenSet<string>.Empty;

    /// <summary>Whether the account holds Owner.</summary>
    public bool IsOwner(string accountId)
    {
        foreach (Assignment assignment in AssignmentsOf(accountId))
        {
            if (assignment.RoleId == BuiltInRoles.OwnerId)
                return true;
        }

        return false;
    }

    /// <summary>The accounts holding Owner that are active, and so can use it.</summary>
    public IEnumerable<string> ActiveOwners() =>
        Assignments
            .Where(a => a.RoleId == BuiltInRoles.OwnerId)
            .Select(a => a.AccountId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => Accounts.TryGetValue(id, out AccessAccount? account) && account.Status == AccountStatus.Active);

    /// <summary>The roles holding a permission.</summary>
    public IEnumerable<Role> RolesHolding(string permissionId) =>
        Roles.Values.Where(r => r.Permissions.Contains(permissionId));

    /// <summary>A snapshot with nothing in it: no accounts, so everybody is refused.</summary>
    public static AuthoritySnapshot Empty { get; } =
        new(0, [], [], [], [], []);
}
