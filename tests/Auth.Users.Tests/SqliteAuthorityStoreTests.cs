using Microsoft.Data.Sqlite;

using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Users.Tests;

/// <summary>
/// The authority tables on a real file: every write checked and applied in one transaction, every
/// cascade reported one change at a time, and the version that orders them.
/// </summary>
public sealed class SqliteAuthorityStoreTests : IDisposable
{
    private const string Start = "kgsm:server.start";
    private const string Console = "kgsm:server.console.read";
    private const string Thresholds = "monitor:thresholds.write";
    private const string Autorun = "assistant:autorun";

    private readonly string _directory;
    private readonly UserStoreOptions _options;
    private readonly SqliteAuthorityStore _store;
    private readonly DateTimeOffset _now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public SqliteAuthorityStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kgsm-authority-" + Guid.NewGuid().ToString("N"));
        _options = new UserStoreOptions { Path = Path.Combine(_directory, "users.db") };
        _store = new SqliteAuthorityStore(_options);
    }

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

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A person account, written the way the version 2 account store writes one.</summary>
    private string Person(string username, string status = UserStatuses.Active)
    {
        string id = UserIds.NewUserId();
        Raw($"""
             INSERT INTO users (user_id, username, username_key, display_name, origin, kind, status, created_utc, updated_utc)
             VALUES ('{id}', '{username}', '{username.ToLowerInvariant()}', '{username}', 'admitted', 'person', '{status}',
                     '2026-09-25T12:00:00.0000000+00:00', '2026-09-25T12:00:00.0000000+00:00');
             """);
        return id;
    }

    private void Raw(string sql)
    {
        using SqliteConnection connection = new($"Data Source={_options.Path};Foreign Keys=True");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private async Task DeclareAsync() => await _store.ReplaceCatalogAsync(
    [
        .. AuthActions.Declared,
        new CatalogAction(Start, "Start servers", ActionEffect.Execute),
        new CatalogAction(Console, "Read consoles", ActionEffect.Read),
        new CatalogAction(Thresholds, "Change thresholds", ActionEffect.Write),
        new CatalogAction(Autorun, "Skip confirmation", ActionEffect.Execute),
    ], _now);

    /// <summary>A catalog, an Owner, and the version to write against next.</summary>
    private async Task<string> OwnerAsync()
    {
        await DeclareAsync();
        string owner = Person("owner");
        await _store.GrantOwnerLocallyAsync("owner", "local:heisen", _now);
        return owner;
    }

    private async Task<AuthorityWrite> ApplyAsync(string actor, AuthorityEdit edit) =>
        await _store.ApplyAsync(actor, edit, await _store.VersionAsync(), _now);

    private async Task<string> RoleAsync(string actor, string name, params string[] actions)
    {
        string permission = (await ApplyAsync(actor, new CreatePermission(name + " actions"))).CreatedId!;
        await ApplyAsync(actor, new SetPermissionActions(permission, actions.ToHashSet()));
        string role = (await ApplyAsync(actor, new CreateRole(name))).CreatedId!;
        await ApplyAsync(actor, new SetRolePermissions(role, new HashSet<string> { permission }));
        return role;
    }

    // ── the file ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFreshFileIsTheCurrentSchemaWithTheBuiltInsAtAuthorityVersionOne()
    {
        AuthoritySnapshot snapshot = await _store.LoadAsync();

        Assert.Equal(1, snapshot.Version);
        Assert.Equal([BuiltInRoles.EveryoneId, BuiltInRoles.OwnerId], snapshot.Roles.Keys.Order());
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_options.Path));
    }

    [Fact]
    public async Task ReopeningTheFileKeepsWhatItHolds()
    {
        string owner = await OwnerAsync();

        AuthoritySnapshot snapshot = await new SqliteAuthorityStore(_options).LoadAsync();

        Assert.True(snapshot.IsOwner(owner));
    }

    // ── versions ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryWriteAdvancesTheVersionByOne()
    {
        string owner = await OwnerAsync();
        long before = await _store.VersionAsync();

        AuthorityWrite write = await ApplyAsync(owner, new CreateRole("Moderator"));

        Assert.Equal(before + 1, write.Version);
        Assert.Equal(before + 1, await _store.VersionAsync());
    }

    [Fact]
    public async Task AWriteAgainstAnOlderVersionIsRefusedWithTheCurrentOne()
    {
        string owner = await OwnerAsync();
        long seen = await _store.VersionAsync();
        await _store.ApplyAsync(owner, new CreateRole("First"), seen, _now);

        StaleAuthorityException e = await Assert.ThrowsAsync<StaleAuthorityException>(
            () => _store.ApplyAsync(owner, new CreateRole("Second"), seen, _now));

        Assert.Equal(seen, e.Expected);
        Assert.Equal(seen + 1, e.Current);
        Assert.DoesNotContain((await _store.LoadAsync()).Roles.Values, r => r.Name == "Second");
    }

    [Fact]
    public async Task ARefusedWriteChangesNothing()
    {
        await OwnerAsync();
        string bob = Person("bob");
        long before = await _store.VersionAsync();

        AuthorityRefusedException e = await Assert.ThrowsAsync<AuthorityRefusedException>(
            () => _store.ApplyAsync(bob, new CreateRole("Mine"), before, _now));

        Assert.Equal(RefusalCode.NotPermitted, e.Refusal.Code);
        Assert.Equal(before, await _store.VersionAsync());
    }

    [Fact]
    public async Task AWriteThatChangesNothingLeavesTheVersionAlone()
    {
        await OwnerAsync();
        long before = await _store.VersionAsync();

        AuthorityWrite again = await _store.GrantOwnerLocallyAsync("owner", "local:heisen", _now);

        Assert.Empty(again.Changes);
        Assert.Equal(before, again.Version);
    }

    // ── roles ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewRoleLandsBelowItsCreatorAndTheRestMoveDown()
    {
        string owner = await OwnerAsync();
        string mod = await RoleAsync(owner, "Moderator", AuthActions.RolesEdit);
        string helper = await RoleAsync(owner, "Helper");
        // Created by an Owner, each lands first; ranking puts them back in order.
        await ApplyAsync(owner, new RankRole(mod, 1));
        string alice = Person("alice");
        await ApplyAsync(owner, new Assign(alice, mod));

        string trusted = (await ApplyAsync(alice, new CreateRole("Trusted"))).CreatedId!;

        AuthoritySnapshot s = await _store.LoadAsync();
        Assert.Equal(1, s.Roles[mod].Rank);
        Assert.Equal(2, s.Roles[trusted].Rank);
        Assert.Equal(3, s.Roles[helper].Rank);
    }

    [Fact]
    public async Task RankingRenumbersTheCustomRolesDensely()
    {
        string owner = await OwnerAsync();
        string c = (await ApplyAsync(owner, new CreateRole("C"))).CreatedId!;
        string b = (await ApplyAsync(owner, new CreateRole("B"))).CreatedId!;
        string a = (await ApplyAsync(owner, new CreateRole("A"))).CreatedId!;

        await ApplyAsync(owner, new RankRole(a, 3));

        AuthoritySnapshot s = await _store.LoadAsync();
        Assert.Equal([b, c, a], s.Roles.Values.Where(r => r.Kind == RoleKind.Custom).OrderBy(r => r.Rank).Select(r => r.RoleId));
        Assert.Equal([1, 2, 3], s.Roles.Values.Where(r => r.Kind == RoleKind.Custom).Select(r => r.Rank).Order());
    }

    [Fact]
    public async Task DeletingARoleRevokesEachAssignmentOfItAndClosesTheGapInTheRanks()
    {
        string owner = await OwnerAsync();
        string first = await RoleAsync(owner, "First", Console);
        string gone = await RoleAsync(owner, "Gone", Start);
        await ApplyAsync(owner, new RankRole(first, 1));
        string alice = Person("alice");
        string bob = Person("bob");
        await ApplyAsync(owner, new Assign(alice, gone));
        await ApplyAsync(owner, new Assign(bob, gone));

        AuthorityWrite write = await ApplyAsync(owner, new DeleteRole(gone));

        Assert.Equal(2, write.Changes.Count(c => c.Kind == AuthorityChangeKind.AssignmentRevoked));
        Assert.Contains(write.Changes, c => c.Kind == AuthorityChangeKind.RoleRemoved && c.Subject == gone);
        AuthoritySnapshot s = await _store.LoadAsync();
        Assert.DoesNotContain(s.Assignments, a => a.RoleId == gone);
        Assert.Equal(1, s.Roles[first].Rank);
    }

    [Fact]
    public async Task ARoleChangeLandsOnTheNextEvaluation()
    {
        string owner = await OwnerAsync();
        string role = await RoleAsync(owner, "Runner", Console);
        string alice = Person("alice");
        await ApplyAsync(owner, new Assign(alice, role));
        Assert.False(new AccessEvaluator(await _store.LoadAsync()).Allows(alice, Start).Allowed);

        string permission = (await _store.LoadAsync()).Roles[role].Permissions.Single();
        await ApplyAsync(owner, new SetPermissionActions(permission, new HashSet<string> { Console, Start }));

        Assert.True(new AccessEvaluator(await _store.LoadAsync()).Allows(alice, Start).Allowed);
    }

    // ── permissions ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletingAPermissionTakesItOutOfEveryRoleHoldingIt()
    {
        string owner = await OwnerAsync();
        string role = await RoleAsync(owner, "Runner", Start);
        string permission = (await _store.LoadAsync()).Roles[role].Permissions.Single();

        AuthorityWrite write = await ApplyAsync(owner, new DeletePermission(permission));

        Assert.Contains(write.Changes, c => c.Kind == AuthorityChangeKind.RoleChanged && c.Subject == role);
        Assert.Contains(write.Changes, c => c.Kind == AuthorityChangeKind.PermissionRemoved && c.Subject == permission);
        Assert.Empty((await _store.LoadAsync()).Roles[role].Permissions);
    }

    [Fact]
    public async Task AnActionNoLongerDeclaredLeavesEveryPermissionThatHeldIt()
    {
        string owner = await OwnerAsync();
        string role = await RoleAsync(owner, "Runner", Start, Console);
        string permission = (await _store.LoadAsync()).Roles[role].Permissions.Single();

        AuthorityWrite write = await _store.ReplaceCatalogAsync(
            [new CatalogAction(Console, "Read consoles", ActionEffect.Read)], _now);

        Assert.Contains(write.Changes, c => c.Kind == AuthorityChangeKind.PermissionChanged && c.Subject == permission);
        AuthoritySnapshot s = await _store.LoadAsync();
        Assert.Equal([Console], s.Permissions[permission].Actions);
        Assert.Equal([Console], s.Catalog.Keys);
    }

    // ── assignments and accounts ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAssignmentRecordsWhoGrantedIt()
    {
        string owner = await OwnerAsync();
        string role = await RoleAsync(owner, "Runner", Start);
        string alice = Person("alice");

        AuthorityWrite write = await ApplyAsync(owner, new Assign(alice, role));

        AuthorityChange granted = Assert.Single(write.Changes);
        Assert.Equal(AuthorityChangeKind.AssignmentGranted, granted.Kind);
        using SqliteConnection connection = new($"Data Source={_options.Path}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT granted_by FROM assignments WHERE assignment_id = '{write.CreatedId}';";
        Assert.Equal(owner, command.ExecuteScalar());
    }

    [Fact]
    public async Task DeletingAnAccountRevokesEachOfItsAssignments()
    {
        string owner = await OwnerAsync();
        string role = await RoleAsync(owner, "Runner", Start);
        string alice = Person("alice");
        string other = await RoleAsync(owner, "Other", Console);
        await ApplyAsync(owner, new Assign(alice, role));
        await ApplyAsync(owner, new Assign(alice, other));

        AuthorityWrite write = await ApplyAsync(owner, new DeleteAccount(alice));

        Assert.Equal(2, write.Changes.Count(c => c.Kind == AuthorityChangeKind.AssignmentRevoked));
        Assert.Contains(write.Changes, c => c.Kind == AuthorityChangeKind.AccountDeleted && c.Subject == alice);
        AuthoritySnapshot s = await _store.LoadAsync();
        Assert.False(s.Accounts.ContainsKey(alice));
        Assert.Empty(s.AssignmentsOf(alice));
    }

    [Fact]
    public async Task TheLastOwnerCannotBeDisabled()
    {
        string owner = await OwnerAsync();

        AuthorityRefusedException e = await Assert.ThrowsAsync<AuthorityRefusedException>(
            () => ApplyAsync(owner, new DisableAccount(owner)));

        Assert.Equal(RefusalCode.LastOwner, e.Refusal.Code);
    }

    [Fact]
    public async Task DisablingAnAccountStopsItFromActing()
    {
        string owner = await OwnerAsync();
        string role = await RoleAsync(owner, "Runner", Start);
        string alice = Person("alice");
        await ApplyAsync(owner, new Assign(alice, role));

        await ApplyAsync(owner, new DisableAccount(alice));

        Assert.Equal(DenyReason.AccountDisabled, new AccessEvaluator(await _store.LoadAsync()).Allows(alice, Start).Reason);
    }

    [Fact]
    public async Task OwnerIsGrantedFromTheShellOnlyToAnExistingAccount()
    {
        await OwnerAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.GrantOwnerLocallyAsync("nobody", "local:heisen", _now));
    }

    [Fact]
    public async Task AShellGrantIsRecordedUnderTheLocalActor()
    {
        await DeclareAsync();
        string bob = Person("bob");

        AuthorityWrite write = await _store.GrantOwnerLocallyAsync("BOB", "local:heisen", _now);

        AuthorityChange granted = Assert.Single(write.Changes);
        Assert.Equal(bob, granted.AccountId);
        Assert.Equal(BuiltInRoles.OwnerId, granted.RoleId);
        Assert.True((await _store.LoadAsync()).IsOwner(bob));
    }

    // ── reports ───────────────────────────────────────────────────────────────────────────────

    private static ActionManifest ReactorManifest(string version, params string[] extra) => new(
        1, "reactor", version,
        [
            new ManifestAction("rules.write", "Change reactor rules", "write", null),
            .. extra.Select(e => new ManifestAction(e, e, "read", null)),
        ]);

    private static ActionManifest Engine() => new(
        1, "kgsm", null,
        [
            new ManifestAction("server.start", "Start servers", "execute", null),
            new ManifestAction("server.console.read", "Read consoles", "read", null),
        ]);

    private static CatalogReport Source(params ActionManifest[] manifests) => new(manifests);

    [Fact]
    public async Task TheCatalogIsTheUnionOfEverySourcesReport()
    {
        await _store.RecordReportAsync("walter", Source(Engine(), ReactorManifest("0.4.0")), _now);
        AuthorityWrite second = await _store.RecordReportAsync("jessie", Source(Engine(), ReactorManifest("0.5.0", "rules.read")), _now);

        IReadOnlyList<CatalogEntry> catalog = await _store.CatalogAsync();
        Assert.Equal(
            ["kgsm:server.console.read", "kgsm:server.start", "reactor:rules.read", "reactor:rules.write"],
            catalog.Select(e => e.Action.Id));
        Assert.Equal(
            [new ActionDeclaration("jessie", "0.5.0"), new ActionDeclaration("walter", "0.4.0")],
            catalog.Single(e => e.Action.Id == "reactor:rules.write").DeclaredBy);
        Assert.Equal([new ActionDeclaration("jessie", "0.5.0")], catalog.Single(e => e.Action.Id == "reactor:rules.read").DeclaredBy);

        Assert.Equal(["reactor:rules.read"], second.Changes.Where(c => c.Kind == AuthorityChangeKind.CatalogActionAdded).Select(c => c.Subject));
        Assert.Equal(4, (await _store.LoadAsync()).Catalog.Count);
    }

    [Fact]
    public async Task ANewActionArrivesUnmappedAndOnlyAnOwnerPerformsIt()
    {
        string owner = Person("owner");
        await _store.GrantOwnerLocallyAsync("owner", "local:heisen", _now);
        string alice = Person("alice");

        await _store.RecordReportAsync("walter", Source(ReactorManifest("0.4.0")), _now);

        AuthoritySnapshot s = await _store.LoadAsync();
        Assert.DoesNotContain(s.Permissions.Values, p => p.Actions.Contains("reactor:rules.write"));
        AccessEvaluator evaluator = new(s);
        Assert.True(evaluator.Allows(owner, "reactor:rules.write").Allowed);
        Assert.Equal(DenyReason.NotGranted, evaluator.Allows(alice, "reactor:rules.write").Reason);
    }

    [Fact]
    public async Task ReportingTheSameThingAgainWritesNothing()
    {
        await _store.RecordReportAsync("walter", Source(Engine(), ReactorManifest("0.4.0")), _now);
        long version = await _store.VersionAsync();

        AuthorityWrite again = await _store.RecordReportAsync("walter", Source(Engine(), ReactorManifest("0.4.0")), _now.AddMinutes(15));

        Assert.Empty(again.Changes);
        Assert.Equal(version, again.Version);
        Assert.Equal(version, await _store.VersionAsync());
    }

    [Fact]
    public async Task AReportOlderThanTheOneHeldIsIgnored()
    {
        await _store.RecordReportAsync("walter", Source(Engine()) with { Sequence = 200 }, _now);

        AuthorityWrite late = await _store.RecordReportAsync("walter", Source(Engine(), ReactorManifest("0.4.0")) with { Sequence = 100 }, _now);

        Assert.Empty(late.Changes);
        Assert.DoesNotContain("reactor:rules.write", (await _store.LoadAsync()).Catalog.Keys);
    }

    [Fact]
    public async Task AForgottenSourceLosesOnlyTheActionsNobodyElseDeclares()
    {
        string owner = Person("owner");
        await _store.GrantOwnerLocallyAsync("owner", "local:heisen", _now);
        await _store.RecordReportAsync("walter", Source(Engine(), ReactorManifest("0.4.0")), _now);
        await _store.RecordReportAsync("jessie", Source(Engine(), ReactorManifest("0.5.0", "rules.read")), _now);

        string permission = (await ApplyAsync(owner, new CreatePermission("Reactor"))).CreatedId!;
        await ApplyAsync(owner, new SetPermissionActions(permission, new HashSet<string> { "reactor:rules.write", "reactor:rules.read" }));

        AuthorityWrite forgotten = await _store.ForgetReportAsync("jessie", _now);

        AuthoritySnapshot s = await _store.LoadAsync();
        Assert.Equal(["reactor:rules.write"], s.Permissions[permission].Actions);
        Assert.DoesNotContain("reactor:rules.read", s.Catalog.Keys);
        Assert.Contains("reactor:rules.write", s.Catalog.Keys);
        Assert.Contains(forgotten.Changes, c => c.Kind == AuthorityChangeKind.PermissionChanged && c.Subject == permission);
        Assert.Equal(["walter"], await _store.ReportingSourcesAsync());
    }

}
