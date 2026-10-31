using System.Net;
using System.Net.Http.Json;

using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>
/// <c>GET /auth/admin/operations</c>: every gated route the provider serves and the action it requires,
/// built from the routes and the edit table the rules enforce — so a client is told exactly what is
/// checked.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class AdminOperationsTests(ServerFixture anchor)
{
    // The daemon's own Program, so the document is built from the routes it really maps.
    private async Task<OperationManifest> ReadAsync()
    {
        HttpResponseMessage response = await anchor.Client.GetAsync("/auth/admin/operations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(AccessJsonContext.Default.OperationManifest))!;
    }

    [Fact]
    public async Task EveryEditKind_IsPublishedOnTheEditsRoute_WithTheActionTheRulesCheck()
    {
        OperationManifest m = await ReadAsync();

        foreach (EditKind kind in EditKind.Wire)
        {
            Operation op = Assert.Single(m.Operations,
                o => o.Method == "POST" && o.Route == AdminOperations.EditsRoute && o.Value == kind.Name);
            Assert.Equal(kind.Action, op.Action);
            Assert.Equal("kind", op.Field);
        }
    }

    [Fact]
    public void EveryPublishedEditKind_ParsesToAnEditOfThatKind()
    {
        // The request parser spells the kinds; the table publishes them. A kind the parser does not
        // read, or reads as another kind, would be published with an action the rules never check.
        foreach (EditKind kind in EditKind.Wire)
        {
            AuthorityEdit? edit = AuthorityEndpoints.ToEdit(new AuthorityEditRequest(
                Version: 1, Kind: kind.Name, RoleId: "r", PermissionId: "p", AccountId: "a", AssignmentId: "x",
                Name: "n", Rank: 1, PermissionIds: ["p"], Actions: ["a:b"], Action: "a:b"));
            Assert.NotNull(edit);
            Assert.Same(kind, edit!.Kind);
        }
    }

    [Fact]
    public async Task TheAccountRoutes_AreEachPublishedWithTheActionTheirHandlerChecks()
    {
        OperationManifest m = await ReadAsync();

        Assert.Contains(m.Operations, o => o is { Method: "POST", Route: "/auth/admin/users" } && o.Action == AuthActions.AccountsCreate);
        Assert.Contains(m.Operations, o => o is { Method: "DELETE", Route: "/auth/admin/users/{userId}" } && o.Action == AuthActions.AccountsDelete);
        Assert.Contains(m.Operations, o => o is { Method: "PATCH", Route: "/auth/admin/users/{userId}", Field: "status", Value: "disabled" }
            && o.Action == AuthActions.AccountsDisable);
        Assert.Contains(m.Operations, o => o is { Method: "PATCH", Route: "/auth/admin/users/{userId}", Field: "status", Value: "active" }
            && o.Action == AuthActions.AccountsApprove);
        Assert.Contains(m.Operations, o => o is { Method: "GET", Route: "/auth/admin/users/{userId}/sessions" } && o.Action == AuthActions.AccountsDisable);
    }

}
