using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// <c>tks-auth owner grant &lt;username&gt;</c>: assign Owner to an existing account from the
/// provider's host.
/// </summary>
/// <remarks>
/// <para>
/// The recovery path for a provider whose every Owner has lost every credential. It adds no power:
/// whoever can run it as the provider's service account already holds the account store. It goes through
/// none of the administration rules for the same reason, and is recorded in the provider's journal like
/// any other grant, with <c>local:&lt;user&gt;</c> as the actor.
/// </para>
/// <para>
/// Run as the provider's service account, so it writes the store and the journal the daemon writes.
/// </para>
/// </remarks>
internal static class OwnerCommand
{
    /// <summary>The first argument that selects this command instead of the daemon.</summary>
    internal const string Verb = "owner";

    private const string Usage = "usage: tks-auth owner grant <username>";

    /// <summary>Run the command. Returns the process's exit code.</summary>
    internal static async Task<int> RunAsync(string[] args, ServerOptions options)
    {
        if (args is not [Verb, "grant", { Length: > 0 } username])
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string actor = ActorStrings.Format(ActorProvider.Local, Environment.UserName);

        SqliteAuthorityStore store;
        try
        {
            store = new SqliteAuthorityStore(new UserStoreOptions { Path = options.UserStorePath });
        }
        catch (UserStoreSchemaException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        AuthorityWrite write;
        try
        {
            write = await store.GrantOwnerLocallyAsync(username, actor, DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        if (write.Changes.Count == 0)
        {
            Console.WriteLine($"{username} already holds Owner.");
            return 0;
        }

        AuthorityChange granted = write.Changes.Single();

        ServiceCollection services = new();
        services.AddLogging(logging => logging.AddSystemdConsole());
        services.AddSingleton<AuditLog>();

        await using (ServiceProvider provider = services.BuildServiceProvider())
        {
            await provider.GetRequiredService<AuditLog>().AssignmentAsync(
                AuthEvents.AssignmentGranted, granted.Subject, granted.AccountId!, username,
                granted.RoleId!, granted.Name, write.Version, actor, origin: null);
        }

        Console.WriteLine($"{username} holds Owner (authority version {write.Version}).");
        return 0;
    }
}
