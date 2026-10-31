using System.Text.Json;

using Microsoft.Data.Sqlite;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>
/// <c>tks-auth owner grant</c>: Owner assigned from the host's shell, and journaled like any
/// other grant.
/// </summary>
public sealed class OwnerCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kgsm-owner-" + Guid.NewGuid().ToString("N"));
    private readonly ServerOptions _options;

    public OwnerCommandTests()
    {
        Directory.CreateDirectory(_root);
        _options = ServerOptions.FromSettings(new ServerSettings { UserStorePath = Path.Combine(_root, "users.db") });
    }

    public void Dispose()
    {
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

    [Fact]
    public async Task OwnerIsGranted()
    {
        string alice = Person("alice");

        int exit = await OwnerCommand.RunAsync(["owner", "grant", "alice"], _options);

        Assert.Equal(0, exit);
        Assert.True((await Store().LoadAsync()).IsOwner(alice));
    }

    [Fact]
    public async Task GrantingAnExistingOwnerChangesNothing()
    {
        Person("alice");
        await OwnerCommand.RunAsync(["owner", "grant", "alice"], _options);
        long version = await Store().VersionAsync();

        Assert.Equal(0, await OwnerCommand.RunAsync(["owner", "grant", "alice"], _options));

        Assert.Equal(version, await Store().VersionAsync());
    }

    [Fact]
    public async Task AnUnknownAccountIsAFailure()
    {
        Person("alice");

        Assert.Equal(1, await OwnerCommand.RunAsync(["owner", "grant", "nobody"], _options));
    }

    [Fact]
    public async Task AStoreAtAnotherSchemaVersionIsRefused()
    {
        using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={_options.UserStorePath};Pooling=False"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO schema_meta (key, value) VALUES ('schema_version', '1');
                """;
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, await OwnerCommand.RunAsync(["owner", "grant", "alice"], _options));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner", "grant")]
    [InlineData("owner", "revoke", "alice")]
    public async Task AnythingButGrantAndAUsernameIsUsage(params string[] args) =>
        Assert.Equal(2, await OwnerCommand.RunAsync(args, _options));
}
