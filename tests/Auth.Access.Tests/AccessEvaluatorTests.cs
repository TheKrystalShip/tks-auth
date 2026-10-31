namespace TheKrystalShip.Auth.Access.Tests;

/// <summary>The one function every access question goes through, in the order its questions are asked.</summary>
public class AccessEvaluatorTests
{
    [Fact]
    public void AnOwnerHoldsEverythingIncludingWhatNobodyDeclared()
    {
        World world = new();
        string owner = world.Owner();

        AccessEvaluator evaluator = world.Evaluator();

        Assert.True(evaluator.Allows(owner, World.Start).Allowed);
        Assert.True(evaluator.Allows(owner, "later:undeclared").Allowed);
        Assert.True(evaluator.HoldsEverything(owner));
    }

    [Fact]
    public void AnActionNobodyDeclaredIsAnOwnersAlone()
    {
        World world = new();
        string alice = world.Person("alice");
        string bob = world.Person("bob");
        world.Assign(alice, world.Role("Everything", 1, World.Start));

        Assert.Equal(DenyReason.UnknownAction, world.Evaluator().Allows(alice, "later:undeclared").Reason);
        Assert.Equal(DenyReason.UnknownAction, world.Evaluator().Allows(bob, "later:undeclared").Reason);
    }

    [Fact]
    public void AnAssignmentGrantsTheActionsOfItsRoleAndNothingMore()
    {
        World world = new();
        string alice = world.Person("alice");
        world.Assign(alice, world.Role("Operator", 1, World.Start));

        AccessEvaluator evaluator = world.Evaluator();

        Assert.True(evaluator.Allows(alice, World.Start).Allowed);
        Assert.Equal(DenyReason.NotGranted, evaluator.Allows(alice, World.Console).Reason);
    }

    [Fact]
    public void EveryoneGrantsItsActionsToEveryActivePerson()
    {
        World world = new();
        world.Everyone(World.Library);
        string alice = world.Person("alice");

        Assert.True(world.Evaluator().Allows(alice, World.Library).Allowed);
    }

    [Fact]
    public void ASelfActionIsHeldByEveryActivePerson()
    {
        World world = new();
        string alice = world.Person("alice");

        Assert.True(world.Evaluator().Allows(alice, World.Prefs).Allowed);
    }

    [Fact]
    public void APendingAccountHoldsNothingAndADisabledOneIsRefused()
    {
        World world = new();
        world.Everyone(World.Library);
        string pending = world.Person("pending", AccountStatus.Pending);
        string disabled = world.Person("disabled", AccountStatus.Disabled);
        AccessEvaluator evaluator = world.Evaluator();

        Assert.Equal(DenyReason.AccountPending, evaluator.Allows(pending, World.Library).Reason);
        Assert.Equal(DenyReason.AccountDisabled, evaluator.Allows(disabled, World.Prefs).Reason);
    }

    [Fact]
    public void ADisabledOwnerHoldsNothing()
    {
        World world = new();
        string owner = world.Person("owner", AccountStatus.Disabled);
        world.Assign(owner, BuiltInRoles.OwnerId);

        Assert.False(world.Evaluator().Allows(owner, World.Start).Allowed);
        Assert.False(world.Evaluator().HoldsEverything(owner));
    }

    [Fact]
    public void AnUnknownAccountAndAMalformedActionAreRefusedForTheirOwnReasons()
    {
        World world = new();
        string alice = world.Person("alice");
        AccessEvaluator evaluator = world.Evaluator();

        Assert.Equal(DenyReason.NoAccount, evaluator.Allows("usr_nobody", World.Library).Reason);
        Assert.Equal(DenyReason.MalformedAction, evaluator.Allows(alice, "not an action").Reason);
    }

    [Fact]
    public void TheEffectiveActionsAreTheCatalogActionsTheAccountMayPerform()
    {
        World world = new();
        string alice = world.Person("alice");
        world.Assign(alice, world.Role("Operator", 1, World.Start));

        IReadOnlySet<string> actions = world.Evaluator().EffectiveActions(alice);

        Assert.Contains(World.Start, actions);
        Assert.Contains(World.Prefs, actions);
        Assert.DoesNotContain(World.Console, actions);
    }
}
