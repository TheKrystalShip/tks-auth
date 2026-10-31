namespace TheKrystalShip.Auth.Access;

/// <summary>
/// One change a person asks to make to who may do what.
/// </summary>
/// <remarks>
/// Every change to permissions, roles, assignments and the standing of an account is one of these. <see cref="AuthorityRules.Check"/> decides whether the asker may make it,
/// and the store applies it only once that has said yes, in the same transaction.
/// </remarks>
public abstract record AuthorityEdit
{
    /// <summary>What kind of edit this is: its name on the wire and the action it needs.</summary>
    public abstract EditKind Kind { get; }
}

/// <summary>
/// A kind of edit: the name a request spells it with and the <c>auth:*</c> action it needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one place an edit's action is written.</b> <see cref="AuthorityRules.Check"/> enforces
/// <see cref="Action"/>, the provider parses requests by <see cref="Name"/>, and the provider publishes
/// <see cref="Wire"/> as its operations — so what a client is told an edit needs is what the rules check.
/// </para>
/// <para>
/// An edit with no <see cref="Name"/> is not made through the edits endpoint: switching an account off
/// and deleting one are the account routes' own, and checked there with the same rules.
/// </para>
/// </remarks>
/// <param name="Name">The request's <c>kind</c>, or null for an edit made only by an account route.</param>
/// <param name="Action">The action the asker must hold.</param>
public sealed record EditKind(string? Name, string Action)
{
    /// <summary>Create a role.</summary>
    public static readonly EditKind RoleCreate = new("role.create", AuthActions.RolesEdit);

    /// <summary>Rename a role.</summary>
    public static readonly EditKind RoleRename = new("role.rename", AuthActions.RolesEdit);

    /// <summary>Delete a role.</summary>
    public static readonly EditKind RoleDelete = new("role.delete", AuthActions.RolesEdit);

    /// <summary>Rank a role.</summary>
    public static readonly EditKind RoleRank = new("role.rank", AuthActions.RolesEdit);

    /// <summary>Set a role's permissions.</summary>
    public static readonly EditKind RolePermissions = new("role.permissions", AuthActions.RolesEdit);

    /// <summary>Make a role hold every action of an application, or stop.</summary>
    public static readonly EditKind RoleEveryAction = new("role.every-action", AuthActions.RolesEdit);

    /// <summary>Create a permission.</summary>
    public static readonly EditKind PermissionCreate = new("permission.create", AuthActions.PermissionsEdit);

    /// <summary>Rename a permission.</summary>
    public static readonly EditKind PermissionRename = new("permission.rename", AuthActions.PermissionsEdit);

    /// <summary>Delete a permission.</summary>
    public static readonly EditKind PermissionDelete = new("permission.delete", AuthActions.PermissionsEdit);

    /// <summary>Set a permission's actions.</summary>
    public static readonly EditKind PermissionActions = new("permission.actions", AuthActions.PermissionsEdit);

    /// <summary>Assign a role.</summary>
    public static readonly EditKind AssignRole = new("assign", AuthActions.RolesAssign);

    /// <summary>Take an assignment away.</summary>
    public static readonly EditKind RevokeAssignment = new("revoke", AuthActions.RolesAssign);

    /// <summary>Switch an account off, or back on.</summary>
    public static readonly EditKind AccountDisable = new(null, AuthActions.AccountsDisable);

    /// <summary>Delete an account.</summary>
    public static readonly EditKind AccountDelete = new(null, AuthActions.AccountsDelete);

    /// <summary>Every kind a request to the edits endpoint may name.</summary>
    public static readonly IReadOnlyList<EditKind> Wire =
    [
        RoleCreate, RoleRename, RoleDelete, RoleRank, RolePermissions, RoleEveryAction,
        PermissionCreate, PermissionRename, PermissionDelete, PermissionActions,
        AssignRole, RevokeAssignment,
    ];
}

/// <summary>Create a role, directly below the creator's highest role.</summary>
public sealed record CreateRole(string Name) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.RoleCreate;
}

/// <summary>Rename a role.</summary>
public sealed record RenameRole(string RoleId, string Name) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.RoleRename;
}

/// <summary>Delete a role, and every assignment of it.</summary>
public sealed record DeleteRole(string RoleId) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.RoleDelete;
}

/// <summary>
/// Move a role to <paramref name="Rank"/> among the custom roles, counting from
/// <see cref="RoleRanks.FirstCustom"/>.
/// </summary>
public sealed record RankRole(string RoleId, int Rank) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.RoleRank;
}

/// <summary>Replace the permissions a role holds.</summary>
public sealed record SetRolePermissions(string RoleId, IReadOnlySet<string> PermissionIds) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.RolePermissions;
}

/// <summary>
/// Make a role hold every action of <paramref name="Application"/>, those declared later included, or
/// with a null application stop doing so.
/// </summary>
public sealed record SetRoleEveryAction(string RoleId, string? Application) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.RoleEveryAction;
}

/// <summary>Create an empty permission.</summary>
public sealed record CreatePermission(string Name) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.PermissionCreate;
}

/// <summary>Rename a permission.</summary>
public sealed record RenamePermission(string PermissionId, string Name) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.PermissionRename;
}

/// <summary>Delete a permission, removing it from every role holding it.</summary>
public sealed record DeletePermission(string PermissionId) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.PermissionDelete;
}

/// <summary>Replace the actions filed into a permission.</summary>
public sealed record SetPermissionActions(string PermissionId, IReadOnlySet<string> Actions) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.PermissionActions;
}

/// <summary>Give an account a role.</summary>
public sealed record Assign(string AccountId, string RoleId) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.AssignRole;
}

/// <summary>Take an assignment away.</summary>
public sealed record Revoke(string AssignmentId) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.RevokeAssignment;
}

/// <summary>Switch an account off.</summary>
public sealed record DisableAccount(string AccountId) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.AccountDisable;
}

/// <summary>Delete an account and every assignment it holds.</summary>
public sealed record DeleteAccount(string AccountId) : AuthorityEdit
{
    /// <inheritdoc />
    public override EditKind Kind => EditKind.AccountDelete;
}

/// <summary>Why a change was refused.</summary>
public enum RefusalCode
{
    /// <summary>The asker lacks the <c>auth:*</c> action the change needs.</summary>
    NotPermitted,

    /// <summary>Only an Owner grants, revokes or acts on Owner.</summary>
    OwnerOnly,

    /// <summary>The built-in roles are not renamed, deleted, ranked or assigned; Owner is not edited.</summary>
    BuiltInRole,

    /// <summary>The role is not ranked below the asker's highest role.</summary>
    RankNotBelow,

    /// <summary>The change would grant actions the asker does not hold.</summary>
    NotHeld,

    /// <summary>A role holding the permission ranks at or above the asker's highest role.</summary>
    PermissionHeldAbove,

    /// <summary>The change would leave no active Owner.</summary>
    LastOwner,

    /// <summary>The name is taken, compared case-insensitively.</summary>
    DuplicateName,

    /// <summary>The name is empty or too long.</summary>
    InvalidName,

    /// <summary>The rank is outside the custom roles.</summary>
    InvalidRank,

    /// <summary>The account already holds that role.</summary>
    DuplicateAssignment,

    /// <summary>A self action or an undeclared one cannot be filed into a permission.</summary>
    NotFileable,

    /// <summary>Something the change names does not exist.</summary>
    NotFound,
}

/// <summary>A refused change, and why.</summary>
/// <param name="Code">The reason.</param>
/// <param name="Message">A sentence for the person who asked.</param>
/// <param name="Actions">For <see cref="RefusalCode.NotHeld"/>, the actions the asker does not hold.</param>
public sealed record AuthorityRefusal(RefusalCode Code, string Message, IReadOnlyList<string>? Actions = null);
