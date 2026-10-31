namespace TheKrystalShip.Auth.Users;

/// <summary>
/// The account store's schema: accounts, and everything that decides access beside them.
/// </summary>
/// <remarks>
/// <para>
/// The store holds permissions, roles, assignments, the catalog of declared actions, the report each
/// source of actions last made and the authority version. What an account may do is its assignments.
/// <c>origin</c> says whether an account arrived by itself or was admitted by somebody.
/// <see cref="SqliteAuthorityStore"/> reads and writes it.
/// </para>
/// <para>
/// Every authority row, and every account, carries the authority version it was written at, which is
/// what a change made against an older version is refused by.
/// </para>
/// </remarks>
public static class AuthoritySchema
{
    /// <summary>The schema version this layout is.</summary>
    public const int Version = 3;

    /// <summary>The version a file is upgraded from when it is opened.</summary>
    public const int PreviousVersion = 2;

    /// <summary>What is appended to the file's name for the copy taken before the upgrade.</summary>
    public const string PreviousVersionBackupSuffix = ".pre-7.0.0";

    /// <summary>The key the authority version is filed under in <c>authority_meta</c>.</summary>
    public const string AuthorityVersionKey = "authority_version";

    /// <summary>
    /// The key of a counter every change to what this file holds advances: what a cached snapshot is
    /// compared against.
    /// </summary>
    public const string GenerationKey = "generation";

    /// <summary>The version the catalog was last replaced at.</summary>
    public const string CatalogVersionKey = "catalog_version";

    /// <summary>The accounts, credentials and lockouts.</summary>
    public const string CreateAccounts = """
        CREATE TABLE IF NOT EXISTS schema_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS users (
            user_id      TEXT PRIMARY KEY,
            username     TEXT NOT NULL,
            username_key TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL,
            origin       TEXT NOT NULL,
            kind         TEXT NOT NULL,
            status       TEXT NOT NULL,
            created_utc  TEXT NOT NULL,
            updated_utc  TEXT NOT NULL,
            version      INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS credentials (
            credential_id TEXT PRIMARY KEY,
            user_id       TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
            kind          TEXT NOT NULL,
            handle        TEXT NOT NULL UNIQUE,
            secret        TEXT NULL,
            label         TEXT NULL,
            created_utc   TEXT NOT NULL,
            last_used_utc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_credentials_user ON credentials(user_id);

        CREATE TABLE IF NOT EXISTS login_failures (
            user_id          TEXT PRIMARY KEY REFERENCES users(user_id) ON DELETE CASCADE,
            failed_count     INTEGER NOT NULL,
            last_failed_utc  TEXT NOT NULL,
            locked_until_utc TEXT NULL
        );
        """;

    /// <summary>
    /// Everything that decides access, created with the accounts in a fresh file.
    /// </summary>
    /// <remarks>
    /// Deleting a role, a permission or an account takes what hangs off it with it through the foreign
    /// keys: a role's assignments and permission links, a permission's links, an account's assignments.
    /// The store reads what a cascade will take before it deletes, so each removal is still reported one
    /// change at a time.
    /// </remarks>
    public const string CreateAuthority = """
        CREATE TABLE IF NOT EXISTS authority_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS permissions (
            permission_id TEXT PRIMARY KEY,
            name          TEXT NOT NULL,
            name_key      TEXT NOT NULL UNIQUE,
            version       INTEGER NOT NULL,
            updated_utc   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS permission_actions (
            permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
            action        TEXT NOT NULL,
            PRIMARY KEY (permission_id, action)
        );

        CREATE TABLE IF NOT EXISTS roles (
            role_id     TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            name_key    TEXT NOT NULL UNIQUE,
            kind        TEXT NOT NULL,
            rank        INTEGER NOT NULL,
            holds_all_of TEXT NULL,
            version     INTEGER NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS role_permissions (
            role_id       TEXT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
            permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
            PRIMARY KEY (role_id, permission_id)
        );

        CREATE TABLE IF NOT EXISTS assignments (
            assignment_id TEXT PRIMARY KEY,
            user_id       TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
            role_id       TEXT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
            granted_by    TEXT NULL,
            version       INTEGER NOT NULL,
            created_utc   TEXT NOT NULL,
            UNIQUE (user_id, role_id)
        );

        CREATE INDEX IF NOT EXISTS ix_assignments_role ON assignments(role_id);

        CREATE TABLE IF NOT EXISTS catalog_actions (
            action  TEXT PRIMARY KEY,
            title   TEXT NOT NULL,
            effect  TEXT NOT NULL,
            self    INTEGER NOT NULL,
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS catalog_reports (
            source       TEXT PRIMARY KEY,
            report       TEXT NOT NULL,
            received_utc TEXT NOT NULL
        );
        """;

    /// <summary>
    /// The two built-in roles, at authority version 1. Owner holds everything implicitly and
    /// <c>everyone</c> starts empty, so neither is linked to a permission here.
    /// </summary>
    internal const string SeedBuiltInRoles = """
        INSERT OR IGNORE INTO roles (role_id, name, name_key, kind, rank, version, updated_utc)
        VALUES ($owner_id, $owner_name, $owner_key, 'owner', $owner_rank, 1, $now),
               ($everyone_id, $everyone_name, $everyone_key, 'everyone', $everyone_rank, 1, $now);

        INSERT OR IGNORE INTO authority_meta (key, value) VALUES ('authority_version', '1');
        INSERT OR IGNORE INTO authority_meta (key, value) VALUES ('generation', '1');
        INSERT OR IGNORE INTO authority_meta (key, value) VALUES ('catalog_version', '1');
        """;
}

/// <summary>Where an account came from, which is what pending-account expiry reads.</summary>
public enum AccountOrigin
{
    /// <summary>Created itself, through registration or a provider. Expires if nobody approves it.</summary>
    Arrived = 0,

    /// <summary>Made or approved by somebody. Never expires.</summary>
    Admitted = 1,
}

/// <summary>Storage strings for the version 2 account columns, and the parse back.</summary>
public static class AccountWire
{
    public const string Arrived = "arrived";
    public const string Admitted = "admitted";
    public const string Person = "person";

    /// <summary>The storage form of an origin.</summary>
    public static string ToWire(AccountOrigin origin) => origin == AccountOrigin.Admitted ? Admitted : Arrived;

    /// <summary>
    /// The origin a string names, with anything unrecognised read as <see cref="AccountOrigin.Arrived"/>
    /// — the origin that expires rather than the one that is spared.
    /// </summary>
    public static AccountOrigin ParseOrigin(string? wire) =>
        wire?.Trim().ToLowerInvariant() == Admitted ? AccountOrigin.Admitted : AccountOrigin.Arrived;
}
