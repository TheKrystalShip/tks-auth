using System.Net;
using System.Text.Json;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>
/// Whether an account administers: the signal <c>/account/me</c> carries, and the one rule that decides
/// whether <c>/admin/</c> is a page or an unknown path.
/// </summary>
[Collection(ServerCollection.Name)]
public sealed class AdministersTests(ServerFixture anchor)
{
    private const string Unknown = "/admin-not-a-route/";

    private string UiRoot => Path.Combine(anchor.Root, "ui");

    private async Task<bool> MeAdministersAsync(ServerFixture.Session session)
    {
        HttpResponseMessage response = await anchor.Client.SendAsync(session.AtTheAccountPage(HttpMethod.Get, "/account/me"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("administers").GetBoolean();
    }

    private async Task<(UserAccount User, ServerFixture.Session Session)> SignedInAsync(UserStatus status, bool owner = false)
    {
        UserAccount user = await anchor.SeedAsync("administers-" + Guid.NewGuid().ToString("N")[..10], "a long enough password", owner, status);
        return (user, await anchor.SignInAsync(user));
    }

    /// <summary>An account holding only <c>auth:roles.assign</c>, at one instance.</summary>
    private async Task<UserAccount> AssignerAtOneInstanceAsync()
    {
        UserAccount owner = await anchor.SeedAsync("administers-root-" + Guid.NewGuid().ToString("N")[..8], "a long enough password", owner: true);
        UserAccount holder = await anchor.SeedAsync("administers-assign-" + Guid.NewGuid().ToString("N")[..8], "a long enough password");
        await anchor.Store.ReplaceCatalogAsync([.. AuthActions.Declared], DateTimeOffset.UtcNow);

        async Task<AuthorityWrite> Edit(AuthorityEdit edit) =>
            await anchor.Store.ApplyAsync(owner.UserId, edit, await anchor.Store.VersionAsync(), DateTimeOffset.UtcNow);

        string permission = (await Edit(new CreatePermission("Assign " + Guid.NewGuid().ToString("N")[..8]))).CreatedId!;
        await Edit(new SetPermissionActions(permission, new HashSet<string> { AuthActions.RolesAssign }));
        string role = (await Edit(new CreateRole("Assigners " + Guid.NewGuid().ToString("N")[..8]))).CreatedId!;
        await Edit(new SetRolePermissions(role, new HashSet<string> { permission }));
        await Edit(new Assign(holder.UserId, role));
        return holder;
    }

    // ── The signal ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_owner_administers()
    {
        var (_, session) = await anchor.SignedInAsync(owner: true, prefix: "administers-owner");

        Assert.True(await MeAdministersAsync(session));
    }

    [Fact]
    public async Task An_account_holding_only_roles_assign_at_one_instance_administers()
    {
        UserAccount holder = await AssignerAtOneInstanceAsync();

        Assert.True(await MeAdministersAsync(await anchor.SignInAsync(holder)));
    }

    [Fact]
    public async Task An_account_holding_nothing_does_not_administer()
    {
        var (_, session) = await anchor.SignedInAsync(prefix: "administers-nothing");

        Assert.False(await MeAdministersAsync(session));
    }

    [Fact]
    public async Task A_pending_account_does_not_administer()
    {
        var (_, session) = await SignedInAsync(UserStatus.Pending);

        Assert.False(await MeAdministersAsync(session));
    }

    // ── The document ──────────────────────────────────────────────────────────

    private sealed record Answer(HttpStatusCode Status, string Body, IReadOnlyList<string> Headers);

    private async Task<Answer> AnswerAsync(string path, ServerFixture.Session? session)
    {
        using var request = session is null
            ? new HttpRequestMessage(HttpMethod.Get, path)
            : session.AtTheAccountPage(HttpMethod.Get, path);
        HttpResponseMessage response = await anchor.Client.SendAsync(request);
        IReadOnlyList<string> headers =
        [
            .. response.Headers.Concat(response.Content.Headers)
                .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"),
        ];
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(), headers);
    }

    private static void AssertSame(Answer expected, Answer actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.Headers, actual.Headers);
    }

    private async Task WithAdminDocumentAsync(Func<Task> body)
    {
        Directory.CreateDirectory(UiRoot);
        File.WriteAllText(Path.Combine(UiRoot, "admin.html"), "<!doctype html><title>bundle admin</title>");
        try
        {
            await body();
        }
        finally
        {
            Directory.Delete(UiRoot, recursive: true);
        }
    }

    [Fact]
    public async Task An_unmatched_path_is_a_bare_not_found()
    {
        Answer unknown = await AnswerAsync(Unknown, session: null);

        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("", unknown.Body);
    }

    [Fact]
    public async Task Without_a_session_the_admin_path_is_an_unknown_path_installed_or_not()
    {
        Answer unknown = await AnswerAsync(Unknown, session: null);

        AssertSame(unknown, await AnswerAsync("/admin/", session: null));
        AssertSame(unknown, await AnswerAsync("/admin/?code=c&state=s", session: null));
        await WithAdminDocumentAsync(async () =>
        {
            AssertSame(unknown, await AnswerAsync("/admin/", session: null));
            AssertSame(unknown, await AnswerAsync("/admin", session: null));
        });
    }

    [Fact]
    public async Task For_somebody_who_does_not_administer_the_admin_path_is_an_unknown_path()
    {
        var (_, session) = await anchor.SignedInAsync(prefix: "administers-plain");
        Answer unknown = await AnswerAsync(Unknown, session);

        AssertSame(unknown, await AnswerAsync("/admin/", session));
        await WithAdminDocumentAsync(async () =>
        {
            AssertSame(unknown, await AnswerAsync("/admin/", session));
            AssertSame(unknown, await AnswerAsync("/admin/?code=c&state=s", session));
        });
    }

    [Fact]
    public async Task For_a_pending_account_the_admin_path_is_an_unknown_path()
    {
        var (_, session) = await SignedInAsync(UserStatus.Pending);
        Answer unknown = await AnswerAsync(Unknown, session);

        await WithAdminDocumentAsync(async () => AssertSame(unknown, await AnswerAsync("/admin/", session)));
    }

    [Fact]
    public async Task An_administrator_is_served_the_document_and_the_callback_with_a_code()
    {
        var (_, owner) = await anchor.SignedInAsync(owner: true, prefix: "administers-doc");
        var scoped = await anchor.SignInAsync(await AssignerAtOneInstanceAsync());

        await WithAdminDocumentAsync(async () =>
        {
            foreach (ServerFixture.Session session in new[] { owner, scoped })
            {
                foreach (string path in new[] { "/admin/", "/admin/?code=c&state=s" })
                {
                    Answer answer = await AnswerAsync(path, session);
                    Assert.Equal(HttpStatusCode.OK, answer.Status);
                    Assert.Contains("bundle admin", answer.Body, StringComparison.Ordinal);
                }
            }
        });
    }
}
