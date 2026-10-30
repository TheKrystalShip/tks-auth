using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Users.Tests;

/// <summary>
/// A role that holds every action of an application, stored beside the roles of a file written before
/// it existed, and the migration that carries a scoped authority to an application's roles.
/// </summary>
public sealed class ApplicationRolesMigrationTests : IDisposable
{
    private const string Stop = "kgsm:server.stop";
    private const string Alerts = "monitor:alerts.read";
    private const string Autorun = "assistant:autorun";

    private static readonly AccessScope Walter = AccessScope.ForNode("walter");
    private static readonly AccessScope Terraria = AccessScope.ForInstance("walter", "terraria", "9f3c");

    private readonly TempStore _temp = new();
    private readonly DateTimeOffset _now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _temp.Dispose();

    private SqliteAuthorityStore Store => _temp.Store;

    private string Person(string username)
    {
        string id = UserIds.NewUserId();
        _temp.Raw($"""
                   INSERT INTO users (user_id, username, username_key, display_name, origin, kind, status, created_utc, updated_utc)
                   VALUES ('{id}', '{username}', '{username}', '{username}', 'admitted', 'person', 'active',
                           '2026-09-25T12:00:00.0000000+00:00', '2026-09-25T12:00:00.0000000+00:00');
                   """);
        return id;
    }

    private async Task<AuthorityWrite> ApplyAsync(string actor, AuthorityEdit edit) =>
        await Store.ApplyAsync(actor, edit, await Store.VersionAsync(), _now);

    private async Task DeclareAsync() => await Store.ReplaceCatalogAsync(
    [
        .. AuthActions.Declared,
        new CatalogAction(Stop, "Stop servers", ActionEffect.Execute, ScopeKind.Instance),
        new CatalogAction(Alerts, "Read alerts", ActionEffect.Read, ScopeKind.Cluster),
        new CatalogAction(Autorun, "Skip confirmation", ActionEffect.Execute, ScopeKind.Cluster),
    ], _now);

    private async Task<string> OwnerAsync(string name = "owner")
    {
        await DeclareAsync();
        string id = Person(name);
        await Store.GrantOwnerLocallyAsync(name, "local:test", _now);
        return id;
    }

    [Fact]
    public async Task TheFlagIsStoredAndReadBackAndReplicated()
    {
        string owner = await OwnerAsync();
        string role = (await ApplyAsync(owner, new CreateRole("Monitor admin"))).CreatedId!;

        await ApplyAsync(owner, new SetRoleEveryAction(role, "monitor"));

        AuthoritySnapshot s = await Store.LoadAsync();
        Assert.Equal("monitor", s.Roles[role].HoldsEveryActionOf);
        Assert.Contains(Alerts, s.ActionsOf(role));
        Assert.Equal("monitor", (await Store.ReadRoleRecordAsync(role))!.HoldsEveryActionOf);

        await ApplyAsync(owner, new SetRoleEveryAction(role, null));

        Assert.Null((await Store.LoadAsync()).Roles[role].HoldsEveryActionOf);
    }

    [Fact]
    public async Task AFileWrittenBeforeTheFlagGainsItsColumnAndKeepsEveryRole()
    {
        string owner = await OwnerAsync();
        string role = (await ApplyAsync(owner, new CreateRole("Operator"))).CreatedId!;
        _temp.Raw("ALTER TABLE roles DROP COLUMN holds_all_of;");

        SqliteAuthorityStore reopened = _temp.OpenAgain();

        AuthoritySnapshot s = await reopened.LoadAsync();
        Assert.Equal("Operator", s.Roles[role].Name);
        Assert.Null(s.Roles[role].HoldsEveryActionOf);
        await reopened.ApplyAsync(owner, new SetRoleEveryAction(role, "kgsm"), await reopened.VersionAsync(), _now);
        Assert.Equal("kgsm", (await reopened.LoadAsync()).Roles[role].HoldsEveryActionOf);
    }

    /// <summary>Two Owners, an Operator at cluster scope, and an Operator at a node and at an instance.</summary>
    private async Task<(string Owner, string Other, string Operator, string Alice, string Bob)> ScopedAuthorityAsync()
    {
        string owner = await OwnerAsync("owner");
        string other = Person("other");
        await Store.GrantOwnerLocallyAsync("other", "local:test", _now);
        string alice = Person("alice");
        string bob = Person("bob");

        string permission = (await ApplyAsync(owner, new CreatePermission("Operating"))).CreatedId!;
        await ApplyAsync(owner, new SetPermissionActions(permission, new HashSet<string> { Stop, Autorun }));
        string op = (await ApplyAsync(owner, new CreateRole("Operator"))).CreatedId!;
        await ApplyAsync(owner, new SetRolePermissions(op, new HashSet<string> { permission }));

        await ApplyAsync(owner, new Assign(alice, op, AccessScope.Cluster));
        await ApplyAsync(owner, new Assign(bob, op, Walter));
        await ApplyAsync(owner, new Assign(bob, op, Terraria));
        return (owner, other, op, alice, bob);
    }

    [Fact]
    public async Task ThePlanNamesWhatItMakesKeepsAndDrops()
    {
        (_, _, string op, _, _) = await ScopedAuthorityAsync();

        ApplicationRolesPlan plan = ApplicationRolesMigration.Plan(await Store.LoadAsync(), "kgsm", "KGSM Administrator");

        Assert.Null(plan.AdministratorRoleId);
        Assert.Equal(["other", "owner"], plan.Administrators.Select(a => a.Name).Order());
        CarriedRole carried = Assert.Single(plan.Carried);
        Assert.Equal((op, "Operator", 1), (carried.RoleId, carried.Name, carried.Holders));
        Assert.Equal(["bob  Operator  instance:walter/terraria#9f3c", "bob  Operator  node:walter"],
            plan.Dropped.Select(d => $"{d.Account}  {d.Role}  {d.Scope}").Order());
    }

    [Fact]
    public async Task PlanningWritesNothing()
    {
        await ScopedAuthorityAsync();
        long version = await Store.VersionAsync();

        ApplicationRolesMigration.Plan(await Store.LoadAsync(), "kgsm", "KGSM Administrator");

        Assert.Equal(version, await Store.VersionAsync());
    }

    [Fact]
    public async Task ApplyingGivesEveryOwnerTheAdministratorRoleAndDropsOnlyTheNarrowGrants()
    {
        (string owner, string other, string op, string alice, string bob) = await ScopedAuthorityAsync();

        AuthorityWrite write = await Store.MigrateToApplicationRolesAsync("kgsm", "KGSM Administrator", "local:test", _now);

        AuthoritySnapshot s = await Store.LoadAsync();
        Role admin = Assert.Single(s.Roles.Values, r => r.Name == "KGSM Administrator");
        Assert.Equal("kgsm", admin.HoldsEveryActionOf);
        Assert.Equal(RoleRanks.FirstCustom, admin.Rank);
        Assert.Equal(RoleRanks.FirstCustom + 1, s.Roles[op].Rank);
        Assert.Equal(admin.RoleId, write.CreatedId);
        foreach (string owners in new[] { owner, other })
            Assert.Contains(s.AssignmentsOf(owners), a => a.RoleId == admin.RoleId && a.Scope == AccessScope.Cluster);

        Assert.Contains(s.AssignmentsOf(alice), a => a.RoleId == op && a.Scope == AccessScope.Cluster);
        Assert.Empty(s.AssignmentsOf(bob));
        Assert.Equal(2, write.Changes.Count(c => c.Kind == AuthorityChangeKind.AssignmentRevoked));
        Assert.Contains(Stop, s.ActionsOf(admin.RoleId));
        Assert.Contains(s.AssignmentsOf(owner), a => a.RoleId == BuiltInRoles.OwnerId);
    }

    [Fact]
    public async Task ApplyingASecondTimeFindsNothingToDo()
    {
        await ScopedAuthorityAsync();
        await Store.MigrateToApplicationRolesAsync("kgsm", "KGSM Administrator", "local:test", _now);
        long version = await Store.VersionAsync();

        ApplicationRolesPlan plan = ApplicationRolesMigration.Plan(await Store.LoadAsync(), "kgsm", "KGSM Administrator");
        AuthorityWrite again = await Store.MigrateToApplicationRolesAsync("kgsm", "KGSM Administrator", "local:test", _now);

        Assert.True(plan.IsEmpty);
        Assert.Empty(again.Changes);
        Assert.Equal(version, again.Version);
    }

    [Fact]
    public async Task ARoleOfThatNameAlreadyThereIsTakenAndFlaggedNotDuplicated()
    {
        string owner = await OwnerAsync();
        string existing = (await ApplyAsync(owner, new CreateRole("KGSM Administrator"))).CreatedId!;

        await Store.MigrateToApplicationRolesAsync("kgsm", "kgsm administrator", "local:test", _now);

        AuthoritySnapshot s = await Store.LoadAsync();
        Assert.Single(s.Roles.Values, r => r.Name == "KGSM Administrator");
        Assert.Equal("kgsm", s.Roles[existing].HoldsEveryActionOf);
    }

    [Fact]
    public async Task ABuiltInRolesNameCannotBeTheAdministrators()
    {
        await OwnerAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store.MigrateToApplicationRolesAsync("kgsm", "Owner", "local:test", _now));
    }
}
