using Microsoft.Data.Sqlite;

namespace TheKrystalShip.Auth.Server;

/// <summary>A device authorization waiting for a person, as the page that approves it reads it.</summary>
/// <param name="ClientId">The client the device asked as.</param>
/// <param name="UserAgent">What the device said it is, or null when it sent nothing.</param>
/// <param name="Name">The name the device sent, cleaned, or null when it sent none.</param>
/// <param name="Expires">When it can no longer be approved.</param>
internal sealed record PendingDevice(string ClientId, string? UserAgent, string? Name, DateTimeOffset Expires);

/// <summary>A device authorization a person approved, taken out of the store by the device redeeming it.</summary>
/// <param name="ClientId">The client the device asked as.</param>
/// <param name="IdentityJson">The identity the approving browser's sign-in was proved with.</param>
/// <param name="UserId">The account that approved it.</param>
/// <param name="Name">The name the person approved it under, or null for none.</param>
internal sealed record ApprovedDevice(string ClientId, string IdentityJson, string UserId, string? Name);

/// <summary>What a device asking at <c>/token</c> is told.</summary>
internal enum DevicePoll
{
    /// <summary>No authorization by that code, or not this client's.</summary>
    Unknown,

    /// <summary>Nobody has decided yet.</summary>
    Pending,

    /// <summary>Nobody has decided yet, and the device asked sooner than its interval allows.</summary>
    SlowDown,

    /// <summary>The person refused it. The row is gone.</summary>
    Denied,

    /// <summary>It ran out before anybody decided. The row is gone.</summary>
    Expired,

    /// <summary>The person approved it. The row is gone, and the approval comes back with it.</summary>
    Approved,
}

/// <summary>
/// The device grant's rows (RFC 8628): one per code a device is showing, until it is redeemed, refused or
/// runs out.
/// </summary>
/// <remarks>
/// <para>
/// Both codes are stored hashed. The device code is the device's bearer until a session exists, and a
/// copy of this file must not be a way to present one; the user code is hashed the same way so neither
/// is held in a form anybody could type.
/// </para>
/// <para>
/// The interval is the row's, not a constant: a device told to slow down is held to the longer one on
/// every poll after, as RFC 8628 §3.5 asks.
/// </para>
/// </remarks>
internal sealed partial class SqliteSessionRegistry
{
    private const string Approved = "approved";
    private const string Denied = "denied";

    private static void InitializeDevice(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS device_authorizations (
                device_code_hash TEXT PRIMARY KEY,
                user_code_hash   TEXT NOT NULL UNIQUE,
                client_id        TEXT NOT NULL,
                user_agent       TEXT NULL,
                device_name      TEXT NULL,
                created          TEXT NOT NULL,
                expires          TEXT NOT NULL,
                interval_s       INTEGER NOT NULL,
                last_poll        TEXT NULL,
                decision         TEXT NULL,
                identity         TEXT NULL,
                user_id          TEXT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Hold a device's codes until a person decides or they run out. False when the user code is already
    /// another device's, which the caller answers by drawing another.
    /// </summary>
    internal Task<bool> CreateDeviceAuthorizationAsync(
        string deviceCodeHash, string userCodeHash, string clientId, string? userAgent, string? name,
        DateTimeOffset now, DateTimeOffset expires, int intervalSeconds, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO device_authorizations
                    (device_code_hash, user_code_hash, client_id, user_agent, device_name, created, expires, interval_s)
                VALUES ($device, $user, $client, $ua, $name, $now, $expires, $interval)
                ON CONFLICT DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$device", deviceCodeHash);
            cmd.Parameters.AddWithValue("$user", userCodeHash);
            cmd.Parameters.AddWithValue("$client", clientId);
            cmd.Parameters.AddWithValue("$ua", (object?)userAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.Parameters.AddWithValue("$expires", expires.ToString("O"));
            cmd.Parameters.AddWithValue("$interval", intervalSeconds);
            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
    }

    /// <summary>The live, undecided authorization a user code names, or null.</summary>
    internal Task<PendingDevice?> FindPendingDeviceAsync(string userCodeHash, CancellationToken ct = default)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT client_id, user_agent, device_name, expires
              FROM device_authorizations
             WHERE user_code_hash = $user AND decision IS NULL AND expires > $now;
            """;
        cmd.Parameters.AddWithValue("$user", userCodeHash);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read()
            ? new PendingDevice(reader.GetString(0), NullableString(reader, 1), NullableString(reader, 2), Parse(reader.GetString(3)))
            : null);
    }

    /// <summary>
    /// Record a person's decision on the live, undecided authorization a user code names. False when there
    /// is none: decided already, run out, or never issued.
    /// </summary>
    /// <param name="userCodeHash">The hash of the user code the person entered.</param>
    /// <param name="approve">Whether they approved it.</param>
    /// <param name="identityJson">The identity their browser's sign-in was proved with.</param>
    /// <param name="userId">Their account.</param>
    /// <param name="name">The name they approved it under, which replaces the one it sent; null for none.</param>
    /// <param name="ct">Cancellation token.</param>
    internal Task<bool> DecideDeviceAsync(
        string userCodeHash, bool approve, string identityJson, string userId, string? name, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE device_authorizations
                   SET decision = $decision, identity = $identity, user_id = $userId, device_name = $name
                 WHERE user_code_hash = $user AND decision IS NULL AND expires > $now;
                """;
            cmd.Parameters.AddWithValue("$decision", approve ? Approved : Denied);
            cmd.Parameters.AddWithValue("$identity", identityJson);
            cmd.Parameters.AddWithValue("$userId", userId);
            cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$user", userCodeHash);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return Task.FromResult(cmd.ExecuteNonQuery() == 1);
        }
    }

    /// <summary>
    /// A device asking whether its code has been decided: the answer, and the approval when there is one.
    /// </summary>
    /// <remarks>
    /// One transaction under the write gate, so an approval is handed to exactly one poll and a decided or
    /// expired row is deleted by the poll that reports it. A poll sooner than the row's interval is told to
    /// slow down and has the interval lengthened by <paramref name="slowDownStep"/>; the poll still counts
    /// as the last one, so a device that keeps hammering keeps being told.
    /// </remarks>
    internal Task<(DevicePoll Outcome, ApprovedDevice? Approval)> PollDeviceAsync(
        string deviceCodeHash, string clientId, DateTimeOffset now, int slowDownStep, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            string? owner, decision, identity, userId, lastPoll, name;
            DateTimeOffset expires;
            int interval;
            using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText =
                    """
                    SELECT client_id, expires, interval_s, last_poll, decision, identity, user_id, device_name
                      FROM device_authorizations WHERE device_code_hash = $device;
                    """;
                read.Parameters.AddWithValue("$device", deviceCodeHash);
                using SqliteDataReader reader = read.ExecuteReader();
                if (!reader.Read())
                    return Task.FromResult<(DevicePoll, ApprovedDevice?)>((DevicePoll.Unknown, null));

                owner = reader.GetString(0);
                expires = Parse(reader.GetString(1));
                interval = reader.GetInt32(2);
                lastPoll = NullableString(reader, 3);
                decision = NullableString(reader, 4);
                identity = NullableString(reader, 5);
                userId = NullableString(reader, 6);
                name = NullableString(reader, 7);
            }

            // Another client's code is not this client's to learn anything about, and asking with it
            // changes nothing.
            if (!string.Equals(owner, clientId, StringComparison.Ordinal))
                return Task.FromResult<(DevicePoll, ApprovedDevice?)>((DevicePoll.Unknown, null));

            DevicePoll outcome;
            ApprovedDevice? approval = null;
            if (expires <= now)
                outcome = DevicePoll.Expired;
            else if (decision == Denied)
                outcome = DevicePoll.Denied;
            else if (decision == Approved && identity is not null && userId is not null)
            {
                outcome = DevicePoll.Approved;
                approval = new ApprovedDevice(owner, identity, userId, name);
            }
            else if (lastPoll is not null && now - Parse(lastPoll) < TimeSpan.FromSeconds(interval))
                outcome = DevicePoll.SlowDown;
            else
                outcome = DevicePoll.Pending;

            using SqliteCommand write = connection.CreateCommand();
            write.Transaction = tx;
            write.Parameters.AddWithValue("$device", deviceCodeHash);
            switch (outcome)
            {
                case DevicePoll.Pending:
                    write.CommandText = "UPDATE device_authorizations SET last_poll = $now WHERE device_code_hash = $device;";
                    write.Parameters.AddWithValue("$now", now.ToString("O"));
                    break;

                case DevicePoll.SlowDown:
                    write.CommandText =
                        "UPDATE device_authorizations SET last_poll = $now, interval_s = interval_s + $step WHERE device_code_hash = $device;";
                    write.Parameters.AddWithValue("$now", now.ToString("O"));
                    write.Parameters.AddWithValue("$step", slowDownStep);
                    break;

                default:
                    write.CommandText = "DELETE FROM device_authorizations WHERE device_code_hash = $device;";
                    break;
            }
            write.ExecuteNonQuery();
            tx.Commit();

            return Task.FromResult<(DevicePoll, ApprovedDevice?)>((outcome, approval));
        }
    }
}
