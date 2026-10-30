using Microsoft.Data.Sqlite;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;
using TheKrystalShip.KGSM.Extensions;

namespace TheKrystalShip.Auth.Anchor.Tests;

/// <summary>
/// <c>tks-auth migrate kgsm</c>: a dry run that writes nothing and a run that applies, journaled under
/// the local user.
/// </summary>
/// <remarks>
/// In the anchor's collection because the journal's state root is a process environment variable.
/// </remarks>
[Collection(AnchorCollection.Name)]
public sealed class MigrateCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kgsm-migrate-" + Guid.NewGuid().ToString("N"));
    private readonly string? _stateRoot = Environment.GetEnvironmentVariable(JournalServiceCollectionExtensions.StateRootVariable);
    private readonly AnchorOptions _options;

    public MigrateCommandTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(JournalServiceCollectionExtensions.StateRootVariable, Path.Combine(_root, "state"));
        _options = AnchorOptions.FromSettings(new AnchorSettings { UserStorePath = Path.Combine(_root, "users.db") });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(JournalServiceCollectionExtensions.StateRootVariable, _stateRoot);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteAuthorityStore Store() => new(new UserStoreOptions { Path = _options.UserStorePath });

    private string Person(string username)
    {
        Store();
        string id = UserIds.NewUserId();
        using SqliteConnection connection = new($"Data Source={_options.UserStorePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
             INSERT INTO users (user_id, username, username_key, display_name, origin, kind, status, created_utc, updated_utc)
             VALUES ('{id}', '{username}', '{username}', '{username}', 'admitted', 'person', 'active', '2026-09-25T12:00:00Z', '2026-09-25T12:00:00Z');
             """;
        command.ExecuteNonQuery();
        return id;
    }

    /// <summary>An Owner, and a person holding a custom role at a node and nowhere else.</summary>
    private async Task<(string Owner, string Bob)> ScopedAsync()
    {
        string owner = Person("owner");
        string bob = Person("bob");
        SqliteAuthorityStore store = Store();
        await store.GrantOwnerLocallyAsync("owner", "local:test", Now);
        string role = (await store.ApplyAsync(owner, new CreateRole("Operator"), await store.VersionAsync(), Now)).CreatedId!;
        await store.ApplyAsync(owner, new Assign(bob, role, AccessScope.ForNode("walter")), await store.VersionAsync(), Now);
        return (owner, bob);
    }

    private string Journal()
    {
        string directory = Path.Combine(_root, "state", AnchorJournal.ProducerId, "events");
        return Directory.Exists(directory)
            ? string.Join('\n', Directory.GetFiles(directory, "*.ndjson").SelectMany(File.ReadAllLines))
            : "";
    }

    private static async Task<(int Exit, string Output)> RunAsync(AnchorOptions options, params string[] args)
    {
        StringWriter output = new();
        int exit = await MigrateCommand.RunAsync(["migrate", "kgsm", .. args], options, output);
        return (exit, output.ToString());
    }

    [Fact]
    public async Task ADryRunPrintsWhatItWouldDoAndWritesNothing()
    {
        await ScopedAsync();
        long version = await Store().VersionAsync();

        (int exit, string output) = await RunAsync(_options);

        Assert.Equal(0, exit);
        Assert.Contains("role 'KGSM Administrator': would be created", output);
        Assert.Contains("granted at cluster scope to 1 Owner(s): owner", output);
        Assert.Contains("bob  Operator  node:walter", output);
        Assert.Contains("nothing was written", output);
        Assert.Equal(version, await Store().VersionAsync());
        Assert.Equal("", Journal());
    }

    [Fact]
    public async Task ApplyingDoesItAndJournalsEachGrantAndRemovalUnderTheLocalUser()
    {
        (string owner, string bob) = await ScopedAsync();

        (int exit, _) = await RunAsync(_options, "--apply");

        Assert.Equal(0, exit);
        AuthoritySnapshot s = await Store().LoadAsync();
        Role admin = Assert.Single(s.Roles.Values, r => r.HoldsEveryActionOf == "kgsm");
        Assert.Contains(s.AssignmentsOf(owner), a => a.RoleId == admin.RoleId);
        Assert.Empty(s.AssignmentsOf(bob));

        string journal = Journal();
        Assert.Contains(AuthEvents.AssignmentGranted, journal);
        Assert.Contains(AuthEvents.AssignmentRevoked, journal);
        Assert.Contains($"local:{Environment.UserName}", journal);
    }

    [Fact]
    public async Task ApplyingTwiceTheSecondFindsNothingToDo()
    {
        await ScopedAsync();
        await RunAsync(_options, "--apply");
        long version = await Store().VersionAsync();

        (int exit, string output) = await RunAsync(_options, "--apply");

        Assert.Equal(0, exit);
        Assert.Contains("nothing to do", output);
        Assert.Equal(version, await Store().VersionAsync());
    }

    [Theory]
    [InlineData("--apply", "--dry-run")]
    [InlineData("--application", "Not An App")]
    [InlineData("--unknown")]
    public async Task ABadOptionIsUsage(params string[] args)
    {
        await ScopedAsync();

        (int exit, _) = await RunAsync(_options, args);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task AnythingButMigrateKgsmIsUsage() =>
        Assert.Equal(2, await MigrateCommand.RunAsync(["migrate", "cinema"], _options, new StringWriter()));
}
