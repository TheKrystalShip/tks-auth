namespace TheKrystalShip.Auth.Access.Tests;

/// <summary>
/// A role that holds every action of an application: what it grants, and who may make or give one.
/// </summary>
public class EveryActionRoleTests
{
    private const string Alerts = "monitor:alerts.read";

    [Fact]
    public void ARoleHoldsEveryCatalogActionInTheApplicationsNamespaceAndNothingElse()
    {
        World world = new();
        string admin = world.EveryActionRole("Monitor admin", 1, "monitor");
        string alice = world.Person("alice");
        world.Assign(alice, admin);

        AccessEvaluator evaluator = world.Evaluator();

        Assert.True(evaluator.Allows(alice, World.Metrics).Allowed);
        Assert.True(evaluator.Allows(alice, World.Thresholds).Allowed);
        Assert.False(evaluator.Allows(alice, World.Start).Allowed);
        Assert.False(evaluator.Allows(alice, World.Library).Allowed);
    }

    [Fact]
    public void AnActionDeclaredLaterIsHeldWithoutAnyoneFilingIt()
    {
        World world = new();
        string admin = world.EveryActionRole("Monitor admin", 1, "monitor");
        string alice = world.Person("alice");
        world.Assign(alice, admin);
        Assert.False(world.Evaluator().Allows(alice, Alerts).Allowed);

        world.Declare(new CatalogAction(Alerts, "Read alerts", ActionEffect.Read));

        Assert.True(world.Evaluator().Allows(alice, Alerts).Allowed);
        Assert.Contains(Alerts, world.Snapshot().ActionsOf(admin));
    }

    [Fact]
    public void ASetRoleEveryActionNeedsTheActionAndTheCallerHoldingWhatItGrants()
    {
        World world = new();
        string owner = world.Owner();
        string editor = world.Person("editor");
        string editorRole = world.Role("Role editor", 1, AuthActions.RolesEdit, World.Metrics);
        world.Assign(editor, editorRole);
        string target = world.Role("Target", 2, World.Console);

        AuthorityRefusal? refused = world.Check(editor, new SetRoleEveryAction(target, "monitor"));

        Assert.Equal(RefusalCode.NotHeld, refused?.Code);
        Assert.Contains(World.Thresholds, refused!.Actions!);
        Assert.Null(world.Check(owner, new SetRoleEveryAction(target, "monitor")));
    }

    [Fact]
    public void ClearingTheApplicationGrantsNothingSoItNeedsNoHolding()
    {
        World world = new();
        string editor = world.Person("editor");
        string editorRole = world.Role("Role editor", 1, AuthActions.RolesEdit);
        world.Assign(editor, editorRole);
        string target = world.EveryActionRole("Monitor admin", 2, "monitor");

        Assert.Null(world.Check(editor, new SetRoleEveryAction(target, null)));
    }

    [Fact]
    public void ABuiltInRoleOrAMalformedApplicationIsRefused()
    {
        World world = new();
        string owner = world.Owner();
        string target = world.Role("Target", 1, World.Console);

        Assert.Equal(RefusalCode.BuiltInRole, world.Check(owner, new SetRoleEveryAction(BuiltInRoles.EveryoneId, "monitor"))?.Code);
        Assert.Equal(RefusalCode.BuiltInRole, world.Check(owner, new SetRoleEveryAction(BuiltInRoles.OwnerId, "monitor"))?.Code);
        Assert.Equal(RefusalCode.NotFound, world.Check(owner, new SetRoleEveryAction(target, "Not An App"))?.Code);
    }

    [Fact]
    public void GivingTheRoleIsBoundedByHoldingEveryActionItGrants()
    {
        World world = new();
        string giver = world.Person("giver");
        string giverRole = world.Role("Giver", 1, AuthActions.RolesAssign, World.Metrics);
        world.Assign(giver, giverRole);
        string admin = world.EveryActionRole("Monitor admin", 2, "monitor");
        string bob = world.Person("bob");

        AuthorityRefusal? refused = world.Check(giver, new Assign(bob, admin));

        Assert.Equal(RefusalCode.NotHeld, refused?.Code);
        Assert.Contains(World.Thresholds, refused!.Actions!);
    }
}
