using Microsoft.Data.Sqlite;

using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Users.Tests;

/// <summary>
/// Opening a file written by the previous schema: what it keeps, what it drops, the copy taken first, and
/// that opening it again changes nothing.
/// </summary>
public sealed class UpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tks-auth-upgrade-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_directory, "accounts.db");

    public UpgradeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string PreviousSchema = """
        CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE users (user_id TEXT PRIMARY KEY, username TEXT NOT NULL, username_key TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL, origin TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL,
            created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE credentials (credential_id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
            kind TEXT NOT NULL, handle TEXT NOT NULL UNIQUE, secret TEXT NULL, label TEXT NULL,
            created_utc TEXT NOT NULL, last_used_utc TEXT NULL);
        CREATE TABLE login_failures (user_id TEXT PRIMARY KEY REFERENCES users(user_id) ON DELETE CASCADE,
            failed_count INTEGER NOT NULL, last_failed_utc TEXT NOT NULL, locked_until_utc TEXT NULL);
        CREATE TABLE authority_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE permissions (permission_id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE,
            version INTEGER NOT NULL, updated_utc TEXT NOT NULL);
        CREATE TABLE permission_actions (permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
            action TEXT NOT NULL, PRIMARY KEY (permission_id, action));
        CREATE TABLE roles (role_id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, kind TEXT NOT NULL,
            rank INTEGER NOT NULL, holds_all_of TEXT NULL, version INTEGER NOT NULL, updated_utc TEXT NOT NULL);
        CREATE TABLE role_permissions (role_id TEXT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
            permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
            PRIMARY KEY (role_id, permission_id));
        CREATE TABLE assignments (assignment_id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
            role_id TEXT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE, scope TEXT NOT NULL, granted_by TEXT NULL,
            version INTEGER NOT NULL, created_utc TEXT NOT NULL, UNIQUE (user_id, role_id, scope));
        CREATE INDEX ix_assignments_role ON assignments(role_id);
        CREATE TABLE service_accounts (user_id TEXT PRIMARY KEY REFERENCES users(user_id) ON DELETE CASCADE,
            component TEXT NOT NULL, member TEXT NOT NULL, UNIQUE (component, member));
        CREATE TABLE service_requirements (user_id TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
            action TEXT NOT NULL, scope_kind TEXT NOT NULL, why TEXT NULL, state TEXT NOT NULL, grant_scope TEXT NULL,
            decided_by TEXT NULL, declared INTEGER NOT NULL, version INTEGER NOT NULL, updated_utc TEXT NOT NULL,
            PRIMARY KEY (user_id, action));
        CREATE TABLE catalog_actions (action TEXT PRIMARY KEY, title TEXT NOT NULL, effect TEXT NOT NULL,
            scope_kind TEXT NOT NULL, self INTEGER NOT NULL, version INTEGER NOT NULL);
        CREATE TABLE member_reports (member TEXT PRIMARY KEY, report TEXT NOT NULL, received_utc TEXT NOT NULL);
        CREATE TABLE authority_outbox (record TEXT PRIMARY KEY, version INTEGER NOT NULL, removed INTEGER NOT NULL);
        CREATE TABLE authority_tombstones (record TEXT PRIMARY KEY, version INTEGER NOT NULL);

        INSERT INTO schema_meta VALUES ('schema_version', '2');
        INSERT INTO authority_meta VALUES ('authority_version', '9'), ('generation', '9'), ('catalog_version', '4');
        INSERT INTO roles (role_id, name, name_key, kind, rank, version, updated_utc) VALUES
            ('role_owner', 'Owner', 'owner', 'owner', 0, 1, 'x'),
            ('role_everyone', 'everyone', 'everyone', 'everyone', 2147483646, 1, 'x'),
            ('role_ops', 'Operator', 'operator', 'custom', 1, 1, 'x');
        INSERT INTO permissions VALUES ('prm_ops', 'Ops', 'ops', 1, 'x');
        INSERT INTO permission_actions VALUES ('prm_ops', 'kgsm:server.start');
        INSERT INTO role_permissions VALUES ('role_ops', 'prm_ops');

        INSERT INTO users VALUES ('usr_alice', 'alice', 'alice', 'Alice', 'admitted', 'person', 'active', 'x', 'x', 1);
        INSERT INTO users VALUES ('usr_bob', 'bob', 'bob', 'Bob', 'admitted', 'person', 'active', 'x', 'x', 1);
        INSERT INTO users VALUES ('usr_svc', 'reactor@walter', 'reactor@walter', 'svc', 'admitted', 'service', 'active', 'x', 'x', 1);
        INSERT INTO credentials VALUES ('c1', 'usr_alice', 'password', 'local:usr_alice', 'hash', NULL, '2026-01-01T00:00:00Z', NULL);
        INSERT INTO service_accounts VALUES ('usr_svc', 'reactor', 'walter');
        INSERT INTO service_requirements VALUES ('usr_svc', 'kgsm:server.start', 'instance', NULL, 'approved', 'node:walter', NULL, 1, 1, 'x');

        INSERT INTO assignments VALUES ('asg_1', 'usr_alice', 'role_owner', 'cluster', NULL, 1, '2026-01-01T00:00:00Z');
        INSERT INTO assignments VALUES ('asg_2', 'usr_bob', 'role_ops', 'cluster', NULL, 1, '2026-01-01T00:00:00Z');
        INSERT INTO assignments VALUES ('asg_3', 'usr_bob', 'role_ops', 'node:walter', NULL, 1, '2026-01-01T00:00:00Z');
        INSERT INTO assignments VALUES ('asg_4', 'usr_svc', 'role_ops', 'cluster', NULL, 1, '2026-01-01T00:00:00Z');

        INSERT INTO catalog_actions VALUES ('kgsm:server.start', 'Start servers', 'execute', 'instance', 0, 1);
        INSERT INTO catalog_actions VALUES ('cinema:watch', 'Watch', 'read', 'cluster', 0, 1);
        INSERT INTO authority_outbox VALUES ('role:role_ops', 9, 0);

        INSERT INTO member_reports VALUES ('walter', '{"anchor":false,"sequence":50,"manifests":[
            {"schemaVersion":1,"component":"kgsm","version":"1.0.0","actions":[{"id":"server.start","title":"Start servers","effect":"execute","scope":"instance"}],"requires":[]},
            {"schemaVersion":1,"component":"auth","version":"6.0.0","actions":[{"id":"roles.edit","title":"Edit roles","effect":"write","scope":"cluster"}]}]}', 'x');
        INSERT INTO member_reports VALUES ('application:cinema', '{"anchor":false,"sequence":60,"manifests":[
            {"schemaVersion":1,"component":"cinema","version":"2.0.0","actions":[{"id":"watch","title":"Watch","effect":"read","scope":"cluster"}]}]}', 'x');
        """;

    private void WritePreviousFile()
    {
        using SqliteConnection connection = new($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = PreviousSchema;
        command.ExecuteNonQuery();
    }

    private long Count(string sql)
    {
        using SqliteConnection connection = new($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private SqliteAuthorityStore Open() => new(new UserStoreOptions { Path = DbPath });

    [Fact]
    public void AFileFromThePreviousSchemaIsCopiedAsideBeforeAnythingIsDropped()
    {
        WritePreviousFile();
        byte[] before = File.ReadAllBytes(DbPath);

        Open();

        string copy = DbPath + AuthoritySchema.PreviousVersionBackupSuffix;
        Assert.True(File.Exists(copy));
        Assert.Equal(before.Length, new FileInfo(copy).Length);
        using SqliteConnection kept = new($"Data Source={copy};Pooling=False");
        kept.Open();
        using SqliteCommand command = kept.CreateCommand();
        command.CommandText = "SELECT count(*) FROM member_reports";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task ServiceAccountsAndScopedAssignmentsGoAndEverythingElseStays()
    {
        WritePreviousFile();

        SqliteAuthorityStore store = Open();
        AuthoritySnapshot s = await store.LoadAsync();

        Assert.Equal(["alice", "bob"], s.Accounts.Values.Select(a => a.Name).Order());
        Assert.Equal(["asg_1", "asg_2"], s.Assignments.Select(a => a.AssignmentId).Order());
        Assert.True(s.IsOwner("usr_alice"));
        Assert.Equal(["role_ops"], s.Roles.Values.Where(r => r.Kind == RoleKind.Custom).Select(r => r.RoleId));
        Assert.Contains("kgsm:server.start", s.Permissions["prm_ops"].Actions);
        Assert.Equal("hash", (await store.FindCredentialAsync("local:usr_alice"))!.Secret);
        Assert.Equal(9L, await store.VersionAsync());

        string note = Assert.Single(store.MigrationNotes);
        Assert.Contains("bob", note);
        Assert.Contains("node:walter", note);
    }

    [Fact]
    public void TheReplicationAndServiceTablesAreDropped()
    {
        WritePreviousFile();

        Open();

        foreach (string table in new[] { "member_reports", "authority_outbox", "authority_tombstones", "service_accounts", "service_requirements" })
            Assert.Equal(0L, Count($"SELECT count(*) FROM sqlite_master WHERE name = '{table}'"));
        Assert.Equal(0L, Count("SELECT count(*) FROM pragma_table_info('assignments') WHERE name = 'scope'"));
        Assert.Equal(0L, Count("SELECT count(*) FROM pragma_table_info('catalog_actions') WHERE name = 'scope_kind'"));
        Assert.Equal(3L, Count("SELECT CAST(value AS INTEGER) FROM schema_meta WHERE key = 'schema_version'"));
    }

    [Fact]
    public async Task WhatMembersReportedStaysInTheCatalogUnderTheKgsmApplication_AndAnApplicationsOwnReportIsKept()
    {
        WritePreviousFile();

        SqliteAuthorityStore store = Open();

        Assert.Equal(["application:cinema", "application:kgsm"], await store.ReportingSourcesAsync());
        IReadOnlyList<CatalogEntry> catalog = await store.CatalogAsync();
        Assert.Equal(["cinema:watch", "kgsm:server.start"], catalog.Select(e => e.Action.Id));
        Assert.DoesNotContain(catalog, e => e.Action.Id.StartsWith("auth:", StringComparison.Ordinal));
    }

    [Fact]
    public void OpeningTheUpgradedFileAgainChangesNothing()
    {
        WritePreviousFile();
        Open();
        long version = Count("SELECT CAST(value AS INTEGER) FROM authority_meta WHERE key = 'authority_version'");
        long assignments = Count("SELECT count(*) FROM assignments");

        SqliteAuthorityStore again = Open();

        Assert.Empty(again.MigrationNotes);
        Assert.Equal(version, Count("SELECT CAST(value AS INTEGER) FROM authority_meta WHERE key = 'authority_version'"));
        Assert.Equal(assignments, Count("SELECT count(*) FROM assignments"));
    }

    [Fact]
    public void AFileAtAnyOtherVersionIsRefused()
    {
        using (SqliteConnection connection = new($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO schema_meta VALUES ('schema_version', '9');";
            command.ExecuteNonQuery();
        }

        Assert.Throws<UserStoreSchemaException>(Open);
    }
}
