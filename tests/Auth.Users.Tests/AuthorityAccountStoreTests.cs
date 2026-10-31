using Microsoft.Data.Sqlite;

using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Users.Tests;

/// <summary>
/// People's accounts, through the account store every sign-in door reads, with every change to an account
/// versioned.
/// </summary>
public sealed class AuthorityAccountStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tks-account-store-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAuthorityStore _store;

    public AuthorityAccountStoreTests()
    {
        _store = new SqliteAuthorityStore(new UserStoreOptions { Path = Path.Combine(_dir, "users.db") });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static UserAccount Person(string username, UserStatus status = UserStatus.Active, AccountOrigin origin = AccountOrigin.Admitted) =>
        new(UserIds.NewUserId(), username, username, origin, status, Now, Now);

    [Fact]
    public async Task AnAccountReadsBackWithItsOrigin()
    {
        UserAccount alice = Person("alice");
        UserAccount bob = Person("bob", UserStatus.Pending, AccountOrigin.Arrived);
        await _store.CreateAsync(alice);
        await _store.CreateAsync(bob);

        UserAccount read = (await _store.FindByUsernameAsync("ALICE"))!;
        Assert.Equal(alice.UserId, read.UserId);
        Assert.Equal(AccountOrigin.Admitted, read.Origin);
        Assert.Equal(AccountOrigin.Arrived, (await _store.FindByIdAsync(bob.UserId))!.Origin);
        Assert.Equal(UserStatus.Pending, (await _store.FindByIdAsync(bob.UserId))!.Status);
    }

    [Fact]
    public async Task ATakenUsernameIsRefusedAsOne()
    {
        await _store.CreateAsync(Person("alice"));

        await Assert.ThrowsAsync<DuplicateUsernameException>(() => _store.CreateAsync(Person("Alice")));
    }

    [Fact]
    public async Task CreatingAndChangingAnAccountAdvancesTheVersion()
    {
        UserAccount alice = Person("alice", UserStatus.Pending);
        await _store.CreateAsync(alice);

        long before = await _store.VersionAsync();
        Assert.True(await _store.UpdateAsync(alice with { Status = UserStatus.Active, Updated = Now.AddMinutes(1) }));

        Assert.Equal(before + 1, await _store.VersionAsync());
        Assert.Equal(UserStatus.Active, (await _store.FindByIdAsync(alice.UserId))!.Status);
    }

    [Fact]
    public async Task AnUpdateToNobodyChangesNothing()
    {
        long before = await _store.VersionAsync();

        Assert.False(await _store.UpdateAsync(Person("ghost")));
        Assert.Equal(before, await _store.VersionAsync());
    }

    [Fact]
    public async Task AttachingAHandleAdvancesTheVersion_SettingASecretDoesNot()
    {
        UserAccount alice = Person("alice");
        await _store.CreateAsync(alice);
        long created = await _store.VersionAsync();

        UserCredential password = new("cred_pw", alice.UserId, CredentialKind.Password, UserCredentials.LocalHandle(alice.UserId),
            "hash", null, Now, null);
        await _store.AddCredentialAsync(password);
        Assert.True(await _store.VersionAsync() > created);
        Assert.Equal(alice.UserId, (await _store.FindByCredentialAsync(password.Handle))!.UserId);

        long before = await _store.VersionAsync();
        Assert.True(await _store.SetCredentialSecretAsync("cred_pw", "hash2"));
        await _store.TouchCredentialAsync("cred_pw", Now);

        Assert.Equal(before, await _store.VersionAsync());
        Assert.Equal("hash2", (await _store.FindCredentialAsync(password.Handle))!.Secret);
    }

    [Fact]
    public async Task AHandleHeldByAnotherAccountIsRefused_AndRemovingOneWorksOnce()
    {
        UserAccount alice = Person("alice");
        UserAccount bob = Person("bob");
        await _store.CreateAsync(alice);
        await _store.CreateAsync(bob);
        await _store.AddCredentialAsync(new UserCredential("c1", alice.UserId, CredentialKind.Identity, "discord:1", null, "a", Now, null));

        await Assert.ThrowsAsync<DuplicateCredentialException>(() =>
            _store.AddCredentialAsync(new UserCredential("c2", bob.UserId, CredentialKind.Identity, "discord:1", null, "b", Now, null)));

        Assert.True(await _store.RemoveCredentialAsync("c1"));
        Assert.False(await _store.RemoveCredentialAsync("c1"));
    }

    [Fact]
    public async Task FailedSignInsLockWithoutMovingTheAuthority()
    {
        UserAccount alice = Person("alice");
        await _store.CreateAsync(alice);
        long before = await _store.VersionAsync();
        LockoutPolicy policy = LockoutPolicy.Default;

        LoginLockout standing = LoginLockout.Clear;
        for (int i = 0; i < 10; i++)
            standing = await _store.RecordFailureAsync(alice.UserId, policy, Now);

        Assert.True(standing.IsLocked(Now));
        Assert.Equal(standing.FailedCount, (await _store.GetLockoutAsync(alice.UserId, policy, Now)).FailedCount);
        Assert.Equal(before, await _store.VersionAsync());

        await _store.ClearLockoutAsync(alice.UserId);
        Assert.False((await _store.GetLockoutAsync(alice.UserId, policy, Now)).IsLocked(Now));
    }

    [Fact]
    public async Task TheSystemsDeletionTakesAssignments()
    {
        UserAccount alice = Person("alice");
        await _store.CreateAsync(alice);
        await _store.GrantOwnerLocallyAsync("alice", "local:test", Now);

        Assert.True(await _store.DeleteAsync(alice.UserId));

        Assert.Null(await _store.FindByIdAsync(alice.UserId));
        Assert.Empty((await _store.LoadAsync()).Assignments);
    }

    [Fact]
    public async Task APasswordSignInWorksAgainstTheStore()
    {
        IUserPasswordHasher hasher = new IdentityPasswordHasher();
        UserAccount alice = Person("alice");
        await _store.CreateAsync(alice);
        await _store.AddCredentialAsync(new UserCredential("cred_pw", alice.UserId, CredentialKind.Password,
            UserCredentials.LocalHandle(alice.UserId), hasher.Hash("correct horse battery"), null, Now, null));

        LocalSignInService signIn = new(_store, hasher);

        Assert.Equal(LocalSignInOutcome.Success, (await signIn.SignInAsync("alice", "correct horse battery", Now)).Outcome);
        Assert.Equal(LocalSignInOutcome.InvalidCredentials, (await signIn.SignInAsync("alice", "wrong", Now)).Outcome);
    }

    [Fact]
    public async Task AnArrivingIdentityIsProvisionedPendingAndArrived()
    {
        IdentityLinkService links = new(_store);

        LinkResult result = await links.ResolveOrProvisionAsync(
            new AccountIdentity("discord", "42", "haru", "Haru", null, []), Now, new PendingPolicy(25, TimeSpan.FromDays(14)));

        Assert.Equal(LinkOutcome.Provisioned, result.Outcome);
        UserAccount user = (await _store.FindByCredentialAsync("discord:42"))!;
        Assert.Equal(UserStatus.Pending, user.Status);
        Assert.Equal(AccountOrigin.Arrived, user.Origin);
        Assert.Equal(AccountStatus.Pending, (await _store.LoadAsync()).Accounts[user.UserId].Status);
    }

    [Fact]
    public async Task AnAccountWithNoPassword_IsFoundByItsLocalHandle()
    {
        IdentityLinkService links = new(_store);
        await links.ResolveOrProvisionAsync(
            new AccountIdentity("discord", "42", "haru", "Haru", null, []), Now, new PendingPolicy(25, TimeSpan.FromDays(14)));
        UserAccount user = (await _store.FindByCredentialAsync("discord:42"))!;

        Assert.Equal(user.UserId, await _store.FindAccountIdByHandleAsync("discord:42"));
        Assert.Equal(user.UserId, await _store.FindAccountIdByHandleAsync(UserCredentials.LocalHandle(user.UserId)));
        Assert.Null(await _store.FindAccountIdByHandleAsync(UserCredentials.LocalHandle("usr_nobody")));
    }
}
