namespace TheKrystalShip.Auth.Users;

/// <summary>
/// Where the account store lives and how it behaves under contention.
/// </summary>
public sealed class UserStoreOptions
{
    /// <summary>
    /// The account store's default path, in the provider's state directory.
    /// </summary>
    /// <remarks>
    /// In a directory the service user owns: SQLite writes <c>-wal</c> and <c>-shm</c> <em>beside</em>
    /// the database, so write permission on the file is not enough — WAL needs the directory.
    /// </remarks>
    public const string DefaultPath = "/var/lib/tks-auth/accounts.db";

    /// <summary>The SQLite file. Created, with its directory, if absent.</summary>
    public string Path { get; set; } = DefaultPath;

    /// <summary>
    /// How long a write waits for another process's write before giving up.
    /// </summary>
    /// <remarks>
    /// The daemon and the host commands (<c>tks-auth owner</c>, <c>tks-auth app</c>) write this file on
    /// their own schedules. WAL lets them read through each other's writes, but two writers still
    /// serialise, and an unconfigured SQLite fails such a collision instantly rather than waiting the few
    /// milliseconds it would take to clear. Both halves are needed; neither is enough alone.
    /// </remarks>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The lockout curve applied to failed passwords.</summary>
    public LockoutPolicy Lockout { get; set; } = LockoutPolicy.Default;
}
