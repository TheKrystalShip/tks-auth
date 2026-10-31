using System.Text.Json;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Server.Tests;

/// <summary>
/// An application whose manifest document lists several components: it owns each of those namespaces, its
/// tokens list their actions, and a role that holds every action of it holds them all.
/// </summary>
/// <remarks>
/// Registered by the host command against the fixture's files, the manifests served by a real listener,
/// as <see cref="ApplicationTests"/> does. Each test makes its own application ids: the store is shared
/// across the collection.
/// </remarks>
[Collection(ServerCollection.Name)]
public sealed class MultiNamespaceApplicationTests(ServerFixture anchor) : IAsyncLifetime
{
    private const string Password = "a long enough password";

    private ApplicationTests.ManifestServer _manifests = null!;

    public async Task InitializeAsync() => _manifests = await ApplicationTests.ManifestServer.StartAsync();

    public async Task DisposeAsync() => await _manifests.DisposeAsync();

    private static string NewId() => "mn" + Guid.NewGuid().ToString("N")[..8];

    private static object Component(string component, params string[] ids) => new
    {
        schemaVersion = 1,
        component,
        version = "1.0.0",
        actions = ids.Select(id => new { id, title = id, effect = "execute", scope = "cluster" }).ToArray(),
        requires = Array.Empty<object>(),
    };

    private string Serve(string name, params object[] components) =>
        _manifests.Serve(name, JsonSerializer.Serialize(new { schemaVersion = 1, version = "1.0.0", manifests = components }));

    private async Task<(int Exit, string Output)> CommandAsync(params string[] args)
    {
        var output = new StringWriter();
        int exit = await ApplicationCommand.RunAsync(args, ServerOptions.FromSettings(new ServerSettings
        {
            UserStorePath = Path.Combine(anchor.Root, "users.db"),
            SessionStorePath = Path.Combine(anchor.Root, "sessions.db"),
            Issuer = ServerFixture.Issuer,
        }), output);
        return (exit, output.ToString());
    }

    private async Task<Application> RegisteredAsync(string id, string manifest)
    {
        (int exit, string output) = await CommandAsync("app", "add", id, "--name", "Multi " + id, "--manifest", manifest);
        Assert.Equal(0, exit);
        Assert.Contains("manifest read:", output);

        var registry = anchor.Service<ApplicationRegistry>();
        for (int attempt = 0; attempt < 50 && registry.Find(id) is null; attempt++)
            await Task.Delay(100);
        return registry.Find(id)!;
    }

    private async Task<string> RoleHoldingEveryActionOfAsync(string application)
    {
        string owner = (await anchor.SeedAsync("mn-owner-" + Guid.NewGuid().ToString("N")[..8], Password, owner: true)).UserId;
        async Task<AuthorityWrite> Apply(AuthorityEdit edit) =>
            await anchor.Store.ApplyAsync(owner, edit, await anchor.Store.VersionAsync(), DateTimeOffset.UtcNow);

        string role = (await Apply(new CreateRole("MN-" + Guid.NewGuid().ToString("N")[..8]))).CreatedId!;
        await Apply(new SetRoleEveryAction(role, application));
        return role;
    }

    [Fact]
    public async Task A_document_listing_components_gives_the_application_each_of_them_to_list_and_to_hold()
    {
        string id = NewId();
        Application application = await RegisteredAsync(id, Serve(id,
            Component(id + "-dns", "records.read", "records.write"), Component(id + "-api", "me.read")));

        string role = await RoleHoldingEveryActionOfAsync(id);
        UserAccount admin = await anchor.SeedAsync("mn-admin-" + Guid.NewGuid().ToString("N")[..8], Password);
        UserAccount plain = await anchor.SeedAsync("mn-plain-" + Guid.NewGuid().ToString("N")[..8], Password);
        await anchor.Store.ApplyAsync(
            (await anchor.Store.LoadAsync()).ActiveOwners().First(), new Assign(admin.UserId, role),
            await anchor.Store.VersionAsync(), DateTimeOffset.UtcNow);

        var catalog = anchor.Service<ApplicationCatalog>();
        string[] expected = [id + "-api:me.read", id + "-dns:records.read", id + "-dns:records.write"];

        Assert.Equal(expected, await catalog.HeldAsync(application, admin.UserId, CancellationToken.None));
        Assert.Empty(await catalog.HeldAsync(application, plain.UserId, CancellationToken.None));
        Assert.Equal(expected, (await anchor.Store.LoadAsync()).ActionsOf(role).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_component_the_application_does_not_own_refuses_the_document_whole()
    {
        string id = NewId();
        string authNamespace = _manifests.Serve(id + "-auth", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            manifests = new[] { Component(id + "-ok", "a"), Component("auth", "roles.edit") },
        }));

        (int exitAuth, string authOutput) = await CommandAsync("app", "add", id + "a", "--name", "Refused", "--manifest", authNamespace);

        Assert.Equal(0, exitAuth);
        Assert.Contains("manifest not taken", authOutput);
        Assert.Contains("this provider's own namespace", authOutput);
        Assert.DoesNotContain(
            await anchor.Store.CatalogAsync(), e => e.Action.Id.StartsWith(id + "-ok:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_application_s_component_is_refused_and_so_is_registering_one_as_an_id()
    {
        string id = NewId();
        string shared = id + "-shared";
        await RegisteredAsync(id, Serve(id, Component(shared, "x")));

        string rival = NewId();
        (_, string refused) = await CommandAsync("app", "add", rival, "--name", "Rival", "--manifest",
            Serve(rival, Component(shared, "y")));
        (int exit, _) = await CommandAsync("app", "add", shared, "--name", "Taken");

        Assert.Contains($"'{shared}' is the namespace of the application '{id}'", refused);
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task The_single_manifest_form_still_owns_only_the_application_s_id()
    {
        string id = NewId();
        string manifest = _manifests.Serve(id, JsonSerializer.Serialize(Component(id, "watch")));

        await RegisteredAsync(id, manifest);

        Assert.Equal(new[] { id }, (await anchor.Store.LoadAsync()).NamespacesOf(id).ToArray());
    }
}
