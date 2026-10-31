namespace TheKrystalShip.Auth.Access;

/// <summary>Whether an account may be used at all.</summary>
/// <remarks>
/// Evaluated before anything else: a pending account authenticates and holds nothing, and a disabled
/// one does not authenticate.
/// </remarks>
public enum AccountStatus
{
    /// <summary>Verified, not yet approved. Holds nothing.</summary>
    Pending = 0,

    /// <summary>Approved.</summary>
    Active = 1,

    /// <summary>Switched off.</summary>
    Disabled = 2,
}

/// <summary>An account, as the evaluator needs to know it.</summary>
/// <param name="AccountId">The opaque account id.</param>
/// <param name="Name">The username, for messages; never a key.</param>
/// <param name="Status">Whether it may be used.</param>
public sealed record AccessAccount(string AccountId, string Name, AccountStatus Status);

/// <summary>A named bundle of actions: the unit a role is built from.</summary>
/// <param name="PermissionId">The opaque permission id.</param>
/// <param name="Name">Unique, compared case-insensitively.</param>
/// <param name="Actions">The action ids it holds.</param>
public sealed record Permission(string PermissionId, string Name, IReadOnlySet<string> Actions);

/// <summary>What sort of role a role is.</summary>
public enum RoleKind
{
    /// <summary>A role a person made.</summary>
    Custom = 0,

    /// <summary>
    /// The built-in role that holds every action, including unmapped ones. Ranked first,
    /// neither editable nor deletable.
    /// </summary>
    Owner = 1,

    /// <summary>
    /// The built-in role every active person holds implicitly. Empty at creation;
    /// its permissions are editable, and it is never assigned.
    /// </summary>
    Everyone = 2,
}

/// <summary>Wire strings for <see cref="RoleKind"/>, and the parse back.</summary>
public static class RoleKinds
{
    public const string Custom = "custom";
    public const string Owner = "owner";
    public const string Everyone = "everyone";

    /// <summary>The wire form of a role kind.</summary>
    public static string ToWire(RoleKind kind) => kind switch
    {
        RoleKind.Owner => Owner,
        RoleKind.Everyone => Everyone,
        _ => Custom,
    };

    /// <summary>
    /// The role kind a string names, with anything unrecognised read as <see cref="RoleKind.Custom"/>:
    /// a custom role grants exactly its permissions and no more.
    /// </summary>
    public static RoleKind Parse(string? wire) => wire?.Trim().ToLowerInvariant() switch
    {
        Owner => RoleKind.Owner,
        Everyone => RoleKind.Everyone,
        _ => RoleKind.Custom,
    };
}

/// <summary>A named, ranked set of permissions.</summary>
/// <param name="RoleId">The opaque role id.</param>
/// <param name="Name">Unique, compared case-insensitively.</param>
/// <param name="Kind">Custom, or one of the built-ins.</param>
/// <param name="Rank">Its place in the total order: lower ranks higher. See <see cref="RoleRanks"/>.</param>
/// <param name="Permissions">The permission ids it holds.</param>
/// <param name="HoldsEveryActionOf">
/// The id of an application whose every action this role holds, or null. The role holds each action
/// the catalog has in that application's namespace, those declared later included, besides what its
/// permissions file.
/// </param>
public sealed record Role(
    string RoleId, string Name, RoleKind Kind, int Rank, IReadOnlySet<string> Permissions,
    string? HoldsEveryActionOf = null);

/// <summary>
/// The fixed ends of the role order. Custom roles take the dense ranks between them.
/// </summary>
/// <remarks>
/// A lower rank is a higher role. Owner is first, then every role a person made in the order they
/// ranked them, then <c>everyone</c>.
/// </remarks>
public static class RoleRanks
{
    /// <summary>Owner's rank. Nothing ranks at or above it.</summary>
    public const int Owner = 0;

    /// <summary>The highest rank a custom role can hold.</summary>
    public const int FirstCustom = 1;

    /// <summary><c>everyone</c>'s rank: below every custom role.</summary>
    public const int Everyone = int.MaxValue - 1;

    /// <summary>The rank of an account that holds no role at all: below <c>everyone</c>.</summary>
    public const int Nothing = int.MaxValue;
}

/// <summary>The ids the two built-in roles are stored under.</summary>
public static class BuiltInRoles
{
    /// <summary>Owner's role id.</summary>
    public const string OwnerId = "role_owner";

    /// <summary><c>everyone</c>'s role id.</summary>
    public const string EveryoneId = "role_everyone";

    /// <summary>Owner's name.</summary>
    public const string OwnerName = "Owner";

    /// <summary><c>everyone</c>'s name.</summary>
    public const string EveryoneName = "everyone";
}

/// <summary>An account holding a role.</summary>
/// <param name="AssignmentId">The opaque assignment id.</param>
/// <param name="AccountId">Who holds it.</param>
/// <param name="RoleId">What they hold.</param>
public sealed record Assignment(string AssignmentId, string AccountId, string RoleId);
