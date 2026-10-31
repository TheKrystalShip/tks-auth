using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Users;

// The one-time upgrade of a file written by the previous schema: the replication tables, the service
// accounts and their requirements, and the node and instance scopes of assignments leave it.
public sealed partial class SqliteAuthorityStore
{
    /// <summary>The component whose actions the provider declares itself.</summary>
    private const string OwnComponent = ActionIds.AuthComponent;

    /// <summary>The application id the actions earlier reported by cluster members are carried under.</summary>
    private const string CarriedApplication = "kgsm";

    /// <summary>
    /// Copy the file aside, whole, before the upgrade changes it. An existing copy is kept: it is the file
    /// as it was before the first upgrade attempt.
    /// </summary>
    private static void BackUp(SqliteConnection connection, string path)
    {
        string copy = path + AuthoritySchema.PreviousVersionBackupSuffix;
        if (File.Exists(copy))
            return;

        Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");

        if (OperatingSystem.IsWindows())
        {
            File.Copy(path, copy);
            return;
        }

        // Created owner-only before a byte is written: the copy holds every password hash.
        using FileStream target = new(copy, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using FileStream source = File.OpenRead(path);
        source.CopyTo(target);
    }

    /// <summary>
    /// Bring a file from the previous schema to this one, in the caller's transaction. Returns one line per
    /// record it removed that held something a person had been given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Accounts, credentials, roles, permissions, cluster-wide assignments and the catalog stay. Service
    /// accounts and everything hanging off them go, and so does every assignment at a node or an instance.
    /// </para>
    /// <para>
    /// What the cluster's members reported is not dropped from the catalog: their manifests are carried
    /// as the report of the <c>kgsm</c> application, one manifest per component, so a permission that holds
    /// one of those actions keeps holding it until that application's own manifest replaces the report.
    /// Reports held for applications stay as they are.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> Upgrade(SqliteConnection connection, SqliteTransaction transaction)
    {
        List<string> notes = [];

        Read(connection, transaction,
            """
            SELECT u.username, r.name, a.scope
            FROM assignments a
            JOIN users u ON u.user_id = a.user_id
            JOIN roles r ON r.role_id = a.role_id
            WHERE a.scope <> 'cluster' AND u.kind = 'person'
            ORDER BY u.username, r.name, a.scope;
            """,
            r => notes.Add($"removed the assignment of '{r.GetString(1)}' to {r.GetString(0)} at {r.GetString(2)}"));

        // A service account takes its assignments, requirements and identity with it.
        Execute(connection, transaction, "DELETE FROM users WHERE kind <> 'person';");

        Execute(connection, transaction,
            """
            CREATE TABLE assignments_next (
                assignment_id TEXT PRIMARY KEY,
                user_id       TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
                role_id       TEXT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
                granted_by    TEXT NULL,
                version       INTEGER NOT NULL,
                created_utc   TEXT NOT NULL,
                UNIQUE (user_id, role_id)
            );

            INSERT OR IGNORE INTO assignments_next (assignment_id, user_id, role_id, granted_by, version, created_utc)
            SELECT assignment_id, user_id, role_id, granted_by, version, created_utc
            FROM assignments WHERE scope = 'cluster' ORDER BY created_utc, assignment_id;

            DROP TABLE assignments;
            ALTER TABLE assignments_next RENAME TO assignments;
            CREATE INDEX ix_assignments_role ON assignments(role_id);

            CREATE TABLE catalog_actions_next (
                action  TEXT PRIMARY KEY,
                title   TEXT NOT NULL,
                effect  TEXT NOT NULL,
                self    INTEGER NOT NULL,
                version INTEGER NOT NULL
            );

            INSERT INTO catalog_actions_next (action, title, effect, self, version)
            SELECT action, title, effect, self, version FROM catalog_actions;

            DROP TABLE catalog_actions;
            ALTER TABLE catalog_actions_next RENAME TO catalog_actions;

            CREATE TABLE catalog_reports (
                source       TEXT PRIMARY KEY,
                report       TEXT NOT NULL,
                received_utc TEXT NOT NULL
            );

            INSERT INTO catalog_reports (source, report, received_utc)
            SELECT member, report, received_utc FROM member_reports WHERE member LIKE 'application:%';
            """);

        CarryMemberManifests(connection, transaction);

        Execute(connection, transaction,
            """
            DROP TABLE service_requirements;
            DROP TABLE service_accounts;
            DROP TABLE member_reports;
            DROP TABLE authority_outbox;
            DROP TABLE authority_tombstones;
            DELETE FROM authority_meta WHERE key IN ('confirmed_utc', 'bound_seconds', 'minimum_contract');
            """);

        Execute(connection, transaction, "UPDATE schema_meta SET value = $v WHERE key = $key;",
            ("$key", UserSchema.VersionKey),
            ("$v", AuthoritySchema.Version.ToString(CultureInfo.InvariantCulture)));

        return notes;
    }

    /// <summary>
    /// File the manifests the cluster's members reported under the carried application's report, the latest
    /// read of each component, unless that application already has a report of its own.
    /// </summary>
    private static void CarryMemberManifests(SqliteConnection connection, SqliteTransaction transaction)
    {
        string source = ApplicationNamespaces.SourcePrefix + CarriedApplication;
        if (Scalar(connection, transaction, "SELECT count(*) FROM catalog_reports WHERE source = $s;", ("$s", source)) is not 0L)
            return;

        Dictionary<string, (long Sequence, ActionManifest Manifest)> latest = new(StringComparer.Ordinal);
        Read(connection, transaction,
            "SELECT report FROM member_reports WHERE member NOT LIKE 'application:%';",
            r =>
            {
                try
                {
                    if (JsonSerializer.Deserialize(r.GetString(0), AccessJsonContext.Default.CatalogReport) is not { } report)
                        return;

                    foreach (ActionManifest manifest in report.Manifests)
                    {
                        if (manifest.Component == OwnComponent)
                            continue;

                        if (!latest.TryGetValue(manifest.Component, out var held) || held.Sequence < report.Sequence)
                            latest[manifest.Component] = (report.Sequence, manifest);
                    }
                }
                catch (JsonException)
                {
                }
            });

        if (latest.Count == 0)
            return;

        CatalogReport carried = new(
            [.. latest.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => l.Value.Manifest)],
            Sequence: latest.Values.Max(l => l.Sequence));

        Execute(connection, transaction,
            "INSERT INTO catalog_reports (source, report, received_utc) VALUES ($s, $r, $now);",
            ("$s", source),
            ("$r", JsonSerializer.Serialize(carried, AccessJsonContext.Default.CatalogReport)),
            ("$now", UserWire.ToWire(DateTimeOffset.UtcNow)));
    }
}
