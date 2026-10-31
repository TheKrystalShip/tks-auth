namespace TheKrystalShip.Auth.Access.Tests;

/// <summary>Which changes to access a caller may make.</summary>
public class AuthorityRulesTests
{
    [Fact]
    public void AnEditNeedsTheActionItIs()
    {
        World world = new();
        string alice = world.Person("alice");

        AuthorityRefusal? refusal = world.Check(alice, new CreateRole("Moderator"));

        Assert.Equal(RefusalCode.NotPermitted, refusal?.Code);
    }

    [Fact]
    public void AnOwnerMayMakeAnyEdit()
    {
        World world = new();
        string owner = world.Owner();

        Assert.Null(world.Check(owner, new CreateRole("Moderator")));
    }

    [Fact]
    public void OnlyAnOwnerGrantsOwner()
    {
        World world = new();
        string editor = world.Person("editor");
        string alice = world.Person("alice");
        world.Assign(editor, world.Role("Assigner", 1, AuthActions.RolesAssign));

        Assert.Equal(RefusalCode.OwnerOnly, world.Check(editor, new Assign(alice, BuiltInRoles.OwnerId))?.Code);
    }

    [Fact]
    public void EveryoneIsNeverAssigned()
    {
        World world = new();
        string owner = world.Owner();
        string alice = world.Person("alice");

        Assert.Equal(RefusalCode.BuiltInRole, world.Check(owner, new Assign(alice, BuiltInRoles.EveryoneId))?.Code);
    }

    [Fact]
    public void ACallerAssignsOnlyRolesRankedBelowTheirOwnHighest()
    {
        World world = new();
        string editor = world.Person("editor");
        string alice = world.Person("alice");
        string above = world.Role("Above", 1, World.Start);
        string own = world.Role("Own", 2, AuthActions.RolesAssign, World.Console);
        string below = world.Role("Below", 3, World.Console);
        world.Assign(editor, own);

        Assert.Equal(RefusalCode.RankNotBelow, world.Check(editor, new Assign(alice, above))?.Code);
        Assert.Equal(RefusalCode.RankNotBelow, world.Check(editor, new Assign(alice, own))?.Code);
        Assert.Null(world.Check(editor, new Assign(alice, below)));
    }

    [Fact]
    public void ACallerGrantsOnlyActionsTheyHold()
    {
        World world = new();
        string editor = world.Person("editor");
        string alice = world.Person("alice");
        string own = world.Role("Own", 1, AuthActions.RolesAssign, World.Console);
        string below = world.Role("Below", 2, World.Start);
        world.Assign(editor, own);

        AuthorityRefusal? refusal = world.Check(editor, new Assign(alice, below));

        Assert.Equal(RefusalCode.NotHeld, refusal?.Code);
        Assert.Equal([World.Start], refusal?.Actions);
    }

    [Fact]
    public void ADuplicateAssignmentIsRefused()
    {
        World world = new();
        string owner = world.Owner();
        string alice = world.Person("alice");
        string role = world.Role("Operator", 1, World.Start);
        world.Assign(alice, role);

        Assert.Equal(RefusalCode.DuplicateAssignment, world.Check(owner, new Assign(alice, role))?.Code);
    }

    [Fact]
    public void TheLastActiveOwnerIsNeverRevokedOrDisabled()
    {
        World world = new();
        string owner = world.Owner();
        string assignment = "asg_0";

        Assert.Equal(RefusalCode.LastOwner, world.Check(owner, new Revoke(assignment))?.Code);
        Assert.Equal(RefusalCode.LastOwner, world.Check(owner, new DisableAccount(owner))?.Code);
    }

    [Fact]
    public void NamesAreUniqueIgnoringCaseAndBounded()
    {
        World world = new();
        string owner = world.Owner();
        world.Role("Operator", 1, World.Start);

        Assert.Equal(RefusalCode.DuplicateName, world.Check(owner, new CreateRole(" operator "))?.Code);
        Assert.Equal(RefusalCode.InvalidName, world.Check(owner, new CreateRole(new string('x', AuthorityRules.MaxNameLength + 1)))?.Code);
    }

    [Fact]
    public void TheBuiltInRolesAreNotRenamedOrDeleted()
    {
        World world = new();
        string owner = world.Owner();

        Assert.Equal(RefusalCode.BuiltInRole, world.Check(owner, new DeleteRole(BuiltInRoles.OwnerId))?.Code);
        Assert.Equal(RefusalCode.BuiltInRole, world.Check(owner, new RenameRole(BuiltInRoles.EveryoneId, "all"))?.Code);
    }

    [Fact]
    public void ASelfActionCannotBeFiledIntoAPermission()
    {
        World world = new();
        string owner = world.Owner();
        string permission = world.Permission("Mine");

        Assert.Equal(RefusalCode.NotFileable,
            world.Check(owner, new SetPermissionActions(permission, new HashSet<string> { World.Prefs }))?.Code);
    }
}
