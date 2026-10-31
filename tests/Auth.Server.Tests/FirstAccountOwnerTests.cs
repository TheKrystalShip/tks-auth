using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>
/// The first account a provider with an empty store makes for itself is an Owner.
/// </summary>
/// <remarks>
/// Read off the fixture's anchor, which started on an empty store, so this is the account a real first
/// start leaves behind rather than one a test assembled.
/// </remarks>
[Collection(ServerCollection.Name)]
public sealed class FirstAccountOwnerTests(ServerFixture anchor)
{
    [Fact]
    public async Task The_first_account_an_empty_store_gets_is_an_Owner()
    {
        UserAccount first = (await anchor.Store.FindByUsernameAsync(FirstAdmin.DefaultUsername))!;

        AuthoritySnapshot s = await anchor.Store.LoadAsync();
        Assert.True(s.IsOwner(first.UserId));
        Assert.True(new AccessEvaluator(s).Allows(first.UserId, AuthActions.RolesEdit).Allowed);
    }
}
