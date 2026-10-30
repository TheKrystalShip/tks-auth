using TheKrystalShip.Auth.Journal;
using TheKrystalShip.Auth.Users;
using TheKrystalShip.KGSM.Extensions;

namespace TheKrystalShip.Auth.Anchor;

/// <summary>
/// <c>tks-auth migrate kgsm</c>: carry the authority to an application's roles, from the provider's host.
/// </summary>
/// <remarks>
/// <para>
/// Without <c>--apply</c> it prints what it would do and writes nothing, so the list of grants it would drop
/// can be read, re-granted where they should stay, and read again. <c>--apply</c> does it, in one
/// transaction, and journals each grant and each removal with <c>local:&lt;user&gt;</c> as the actor.
/// What it does is <see cref="ApplicationRolesMigration"/>.
/// </para>
/// <para>
/// It touches the roles and assignments only. Accounts, their credentials and Discord links, and the
/// signing key are left as they are, so subjects and key ids carry over. Run as the provider's service
/// account, so it writes the files the daemon writes.
/// </para>
/// </remarks>
internal static class MigrateCommand
{
    /// <summary>The first argument that selects this command instead of the daemon.</summary>
    internal const string Verb = "migrate";

    /// <summary>The application the authority moves under when none is named.</summary>
    internal const string DefaultApplication = "kgsm";

    /// <summary>The administrator role's name when none is given.</summary>
    internal const string DefaultAdministrator = "KGSM Administrator";

    private const string Usage =
        "usage: tks-auth migrate kgsm [--apply | --dry-run] [--application <id>] [--administrator <role name>]";

    /// <summary>Run the command. Returns the process's exit code.</summary>
    internal static async Task<int> RunAsync(string[] args, AnchorOptions options, TextWriter? output = null)
    {
        TextWriter stdout = output ?? Console.Out;

        if (args is not [Verb, "kgsm", .. var rest] || Parse(rest) is not { } o)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (o.Apply && o.DryRun)
        {
            Console.Error.WriteLine("--apply and --dry-run are opposites.");
            return 2;
        }

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

        ApplicationRolesPlan plan;
        try
        {
            plan = ApplicationRolesMigration.Plan(await store.LoadAsync(), o.Application, o.Administrator);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        if (!o.Apply)
        {
            Describe(stdout, plan, options.UserStorePath, applied: false);
            stdout.WriteLine("dry run: nothing was written; --apply does it.");
            return 0;
        }

        if (plan.IsEmpty)
        {
            Describe(stdout, plan, options.UserStorePath, applied: true);
            stdout.WriteLine("nothing to do.");
            return 0;
        }

        string actor = KgsmActor.Format(KgsmActorProvider.Local, Environment.UserName);
        AuthorityWrite write = await store.MigrateToApplicationRolesAsync(o.Application, o.Administrator, actor, DateTimeOffset.UtcNow);

        ServiceCollection services = new();
        services.AddLogging(logging => logging.AddSystemdConsole());
        services.AddKgsmJournal(AnchorJournal.ProducerId, typeof(AnchorJournal).Assembly);
        services.AddSingleton<AnchorJournal>();
        await using (ServiceProvider provider = services.BuildServiceProvider())
        {
            await AuthorityJournaling.JournalAsync(provider.GetRequiredService<AnchorJournal>(), write, actor,
                origin: null, member: null, CancellationToken.None);
        }

        Describe(stdout, plan, options.UserStorePath, applied: true);
        stdout.WriteLine($"applied at authority version {write.Version}: {write.Changes.Count} change(s) journaled.");
        return 0;
    }

    private static void Describe(TextWriter stdout, ApplicationRolesPlan plan, string store, bool applied)
    {
        string be = applied ? "was" : "would be";
        stdout.WriteLine($"store {store}");
        stdout.WriteLine(plan.AdministratorRoleId is null
            ? $"role '{plan.AdministratorName}': {be} created, first among the custom roles, holding every action of '{plan.Application}'"
            : plan.FlagsAdministrator
                ? $"role '{plan.AdministratorName}': exists; {be} made to hold every action of '{plan.Application}'"
                : $"role '{plan.AdministratorName}': exists and holds every action of '{plan.Application}'");

        stdout.WriteLine(plan.Administrators.Count == 0
            ? "administrator role: every Owner holds it already"
            : $"administrator role: {be} granted at cluster scope to {plan.Administrators.Count} Owner(s): "
              + string.Join(", ", plan.Administrators.Select(a => a.Name)));

        stdout.WriteLine("kept as they are (cluster scope):");
        foreach (CarriedRole role in plan.Carried)
            stdout.WriteLine($"  {role.Name}: {role.Holders} holder(s)");
        if (plan.Carried.Count == 0)
            stdout.WriteLine("  no custom roles");

        stdout.WriteLine(plan.Dropped.Count == 0
            ? "dropped (node or instance scope): none"
            : $"dropped (node or instance scope): {plan.Dropped.Count}, {(applied ? "removed" : "to remove")}");
        foreach (DroppedAssignment d in plan.Dropped)
            stdout.WriteLine($"  {d.Account}  {d.Role}  {d.Scope}");
    }

    private sealed record Options(string Application, string Administrator, bool Apply, bool DryRun);

    private static Options? Parse(IReadOnlyList<string> args)
    {
        string application = DefaultApplication;
        string administrator = DefaultAdministrator;
        bool apply = false;
        bool dryRun = false;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--apply":
                    apply = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--application" when i + 1 < args.Count:
                    application = args[++i].Trim();
                    break;
                case "--administrator" when i + 1 < args.Count:
                    administrator = args[++i].Trim();
                    break;
                default:
                    Console.Error.WriteLine($"'{args[i]}' is not an option here, or is missing its value.");
                    return null;
            }
        }

        if (!TheKrystalShip.Auth.Access.ActionIds.IsValid(application + ":x") || administrator.Length == 0)
        {
            Console.Error.WriteLine("An application id is lowercase letters, digits and '-'; the administrator role needs a name.");
            return null;
        }

        return new Options(application, administrator, apply, dryRun);
    }
}
