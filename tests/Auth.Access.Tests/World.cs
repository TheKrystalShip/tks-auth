namespace TheKrystalShip.Auth.Access.Tests;

/// <summary>
/// An authority, built up line by line, so a test states only what it is about.
/// </summary>
/// <remarks>
/// The catalog starts with the provider's own actions and a handful of actions of each effect, which is
/// what most tests need to have something to grant.
/// </remarks>
internal sealed class World
{
    public const string Start = "kgsm:server.start";
    public const string Console = "kgsm:server.console.read";
    public const string Library = "kgsm:library.read";
    public const string Metrics = "monitor:metrics.read";
    public const string Thresholds = "monitor:thresholds.write";
    public const string Prefs = "api:preferences.write";
    public const string Autorun = "assistant:autorun";

    private readonly List<AccessAccount> _accounts = [];
    private readonly List<CatalogAction> _catalog =
    [
        .. AuthActions.Declared,
        new(Start, "Start servers", ActionEffect.Execute),
        new(Console, "Read consoles", ActionEffect.Read),
        new(Library, "Read the library", ActionEffect.Read),
        new(Metrics, "Read metrics", ActionEffect.Read),
        new(Thresholds, "Change thresholds", ActionEffect.Write),
        new(Prefs, "Change own preferences", ActionEffect.Write, Self: true),
        new(Autorun, "Skip confirmation", ActionEffect.Execute),
    ];
    private readonly List<Permission> _permissions = [];
    private readonly List<Role> _roles =
    [
        new(BuiltInRoles.OwnerId, BuiltInRoles.OwnerName, RoleKind.Owner, RoleRanks.Owner, new HashSet<string>()),
        new(BuiltInRoles.EveryoneId, BuiltInRoles.EveryoneName, RoleKind.Everyone, RoleRanks.Everyone, new HashSet<string>()),
    ];
    private readonly List<Assignment> _assignments = [];
    private int _next;

    public string Person(string name, AccountStatus status = AccountStatus.Active)
    {
        string id = "usr_" + name;
        _accounts.Add(new AccessAccount(id, name, status));
        return id;
    }

    public string Owner(string name = "owner")
    {
        string id = Person(name);
        Assign(id, BuiltInRoles.OwnerId);
        return id;
    }

    public World Declare(CatalogAction action)
    {
        _catalog.Add(action);
        return this;
    }

    public string Permission(string name, params string[] actions)
    {
        string id = "prm_" + name.Replace(' ', '_');
        _permissions.Add(new Permission(id, name, actions.ToHashSet()));
        return id;
    }

    /// <summary>A custom role at <paramref name="rank"/>, holding one permission per action given.</summary>
    public string Role(string name, int rank, params string[] actions)
    {
        string id = "role_" + name.Replace(' ', '_');
        string permission = Permission(name + " actions", actions);
        _roles.Add(new Role(id, name, RoleKind.Custom, rank, new HashSet<string> { permission }));
        return id;
    }

    /// <summary>A custom role at <paramref name="rank"/> that holds every action of <paramref name="application"/>.</summary>
    public string EveryActionRole(string name, int rank, string application)
    {
        string id = "role_" + name.Replace(' ', '_');
        _roles.Add(new Role(id, name, RoleKind.Custom, rank, new HashSet<string>(), application));
        return id;
    }

    public void Everyone(params string[] actions)
    {
        string permission = Permission("everyone actions", actions);
        int at = _roles.FindIndex(r => r.RoleId == BuiltInRoles.EveryoneId);
        _roles[at] = _roles[at] with { Permissions = new HashSet<string> { permission } };
    }

    public string Assign(string account, string role)
    {
        string id = "asg_" + _next++;
        _assignments.Add(new Assignment(id, account, role));
        return id;
    }

    public AuthoritySnapshot Snapshot() => new(7, _accounts, _catalog, _permissions, _roles, _assignments);

    public AccessEvaluator Evaluator() => new(Snapshot());

    public AuthorityRefusal? Check(string actor, AuthorityEdit edit) => AuthorityRules.Check(Snapshot(), actor, edit);
}
