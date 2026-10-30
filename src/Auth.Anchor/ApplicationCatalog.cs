using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Auth.Anchor;

/// <summary>What the last reading of an application's manifest came to.</summary>
/// <param name="At">When it was read.</param>
/// <param name="Problem">Why it was not taken, or null when it was.</param>
/// <param name="Actions">How many actions it declared, when it was taken.</param>
internal sealed record ManifestState(DateTimeOffset At, string? Problem, int Actions)
{
    /// <summary>Whether its actions are in the catalog as it declared them.</summary>
    public bool Taken => Problem is null;
}

/// <summary>
/// An application's actions: read from the manifest it serves, kept in the catalog under it, and listed
/// in its tokens.
/// </summary>
/// <remarks>
/// <para>
/// <b>tks-auth pulls; the application declares.</b> An application serves its manifest — the format KGSM's
/// components ship (<c>kgsm-docs/reference/action-manifest.md</c>) — at the address it was registered with.
/// It is read when the application is registered, when the address changes and on an interval, and lands
/// in the catalog through <see cref="AuthorityIntake"/> as a report held under
/// <see cref="Application.CatalogMember"/>, beside every member's. An action new to the catalog arrives
/// unmapped, as a KGSM component's does, and is grantable only once somebody files it into a permission.
/// </para>
/// <para>
/// <b>An application speaks only for namespaces it owns.</b> The document it serves is one manifest, in the
/// namespace of its id, or a <c>manifests</c> list with one manifest per component, for an application whose
/// actions span several namespaces; it then owns its id and each of those components. A component that is
/// <c>auth</c>, a KGSM component's, or another application's is refused whole: an application declaring
/// <c>kgsm:server.stop</c> would otherwise be a way to put a KGSM action under somebody else's name. A
/// manifest declaring requirements is refused too, since an application outside KGSM has no service
/// account here, and so is one declaring an action at a node or an instance, which an application has
/// none of.
/// </para>
/// <para>
/// <b>A manifest that cannot be read changes nothing.</b> The last one taken stays, so an application whose
/// server is down for a minute keeps every action and every grant it had.
/// </para>
/// </remarks>
internal sealed class ApplicationCatalog(
    IHttpClientFactory http,
    AuthorityIntake intake,
    AnchorAuthority authority,
    TimeProvider clock,
    ILogger<ApplicationCatalog> logger)
{
    /// <summary>The named client manifests are read with.</summary>
    internal const string HttpClientName = "application-manifests";

    /// <summary>The largest manifest read. A manifest is a list of titles; a few kilobytes is a large one.</summary>
    internal const int MaxManifestBytes = 256 * 1024;

    private readonly ConcurrentDictionary<string, ManifestState> _states = new(StringComparer.Ordinal);

    /// <summary>What the last reading of an application's manifest came to in this process, or null.</summary>
    public ManifestState? StateOf(Application application) =>
        _states.TryGetValue(application.Id, out ManifestState? state) ? state : null;

    /// <summary>Read an application's manifest and bring the catalog in line with it.</summary>
    public async Task<ManifestState> RefreshAsync(Application application, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (application.ManifestUrl is not { } address)
            return Record(application, new ManifestState(now, "The application has no manifest address.", 0));

        (ApplicationManifestDocument? document, string? problem) = await ReadAsync(address, ct).ConfigureAwait(false);
        if (document is null)
            return Record(application, new ManifestState(now, problem, 0));

        IReadOnlyList<ActionManifest> manifests = document.Parts();
        if (await RefusalAsync(application, document, manifests, ct).ConfigureAwait(false) is { } refused)
            return Record(application, new ManifestState(now, refused, 0));

        await intake.CatalogDeclaredAsync(
            application.CatalogMember,
            new MemberCatalogReport(Anchor: false, manifests, Sequence: now.ToUnixTimeMilliseconds()),
            ct).ConfigureAwait(false);

        return Record(application, new ManifestState(now, null, manifests.Sum(m => m.CatalogActions().Count())));
    }

    /// <summary>Take everything an application declared out of the catalog and every permission.</summary>
    public async Task ForgetAsync(Application application, CancellationToken ct)
    {
        _states.TryRemove(application.Id, out _);
        await intake.MemberRemovedAsync(application.CatalogMember, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="component"/> is a namespace a KGSM component declares actions in: one a
    /// member's report, rather than an application's, carries.
    /// </summary>
    public async Task<bool> IsKgsmNamespaceAsync(string component, CancellationToken ct)
    {
        if (component is Application.KgsmId or ActionIds.AuthComponent)
            return true;
        if (authority.Store is not { } store)
            return false;

        foreach (CatalogEntry entry in await store.CatalogAsync(ct).ConfigureAwait(false))
        {
            if (ActionIds.TryParse(entry.Action.Id, out string declaredIn, out _)
                && declaredIn == component
                && entry.DeclaredBy.Any(d => !d.Member.StartsWith(Application.CatalogMemberPrefix, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The application's actions <paramref name="accountId"/> holds, evaluated now: what its access token
    /// lists.
    /// </summary>
    /// <remarks>
    /// Every action in the catalog in a namespace the application owns (its id, and each component its
    /// manifests declare), asked of the one evaluator at the whole organization's scope, which the store
    /// records as <c>cluster</c>. Self actions are held by every active person and are listed with the
    /// rest; an Owner holds every one.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The store holds no authority.</exception>
    public async Task<IReadOnlyList<string>> HeldAsync(Application application, string accountId, CancellationToken ct)
    {
        AuthoritySource source = authority.Source
            ?? throw new InvalidOperationException(authority.UnavailableReason ?? "The account store holds no authority.");

        var evaluator = new AccessEvaluator(await source.CurrentAsync(ct).ConfigureAwait(false), clock);
        return [.. evaluator.Snapshot.ActionsOfApplication(application.Id)
            .Where(id => evaluator.Allows(accountId, id, AccessScope.Cluster).Allowed)
            .Order(StringComparer.Ordinal)];
    }

    private async Task<(ApplicationManifestDocument? Document, string? Problem)> ReadAsync(string address, CancellationToken ct)
    {
        try
        {
            HttpClient client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"{address} answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxManifestBytes)
                return (null, $"{address} is larger than a manifest can be.");

            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] bytes = new byte[MaxManifestBytes + 1];
            int read = 0;
            while (read < bytes.Length
                   && await body.ReadAsync(bytes.AsMemory(read), ct).ConfigureAwait(false) is var n and > 0)
                read += n;
            if (read > MaxManifestBytes)
                return (null, $"{address} is larger than a manifest can be.");

            ApplicationManifestDocument? document = JsonSerializer.Deserialize(
                bytes.AsSpan(0, read), AccessJsonContext.Default.ApplicationManifestDocument);
            if (document is null)
                return (null, $"{address} served no manifest.");

            int stale = new[] { document.SchemaVersion }.Concat((document.Manifests ?? []).Select(m => m.SchemaVersion))
                .FirstOrDefault(v => v != ActionManifest.SupportedSchemaVersion, ActionManifest.SupportedSchemaVersion);
            if (stale != ActionManifest.SupportedSchemaVersion)
            {
                return (null, $"{address} is schema version {stale}, and this build reads "
                              + $"{ActionManifest.SupportedSchemaVersion}.");
            }

            return (document, null);
        }
        catch (JsonException e)
        {
            return (null, $"{address} is not a manifest: {e.Message}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, $"{address} could not be read: {e.Message}");
        }
    }

    private async Task<string?> RefusalAsync(
        Application application, ApplicationManifestDocument document, IReadOnlyList<ActionManifest> manifests, CancellationToken ct)
    {
        if (manifests.Count == 0)
            return "The document declares no component.";

        // One manifest speaks for the application's own namespace. Several each speak for a namespace of
        // their own, which no other application and no KGSM component holds.
        if (!document.IsPlural)
        {
            ActionManifest manifest = manifests[0];
            if (manifest.Component != application.Id)
            {
                return $"The manifest declares actions in '{manifest.Component}', and this application's namespace is "
                       + $"'{application.Id}'.";
            }

            if (await IsKgsmNamespaceAsync(manifest.Component, ct).ConfigureAwait(false))
                return $"'{manifest.Component}' is a KGSM component's namespace.";
        }
        else
        {
            foreach (string component in manifests.Select(m => m.Component))
            {
                if (!ActionIds.IsValid(component + ":x"))
                    return $"'{component}' is not a component namespace.";
                if (manifests.Count(m => m.Component == component) > 1)
                    return $"'{component}' is declared more than once.";
                if (component == ActionIds.AuthComponent)
                    return $"'{component}' is this provider's own namespace.";
                if (await IsKgsmNamespaceAsync(component, ct).ConfigureAwait(false))
                    return $"'{component}' is a KGSM component's namespace.";
                if (await OwnerOfAsync(component, ct).ConfigureAwait(false) is { } owner && owner != application.Id)
                    return $"'{component}' is the namespace of the application '{owner}'.";
            }
        }

        if (manifests.Any(m => m.Requires is { Count: > 0 }))
            return "An application's manifest declares no requirements: it has no service account here.";

        if (manifests.SelectMany(m => (m.Actions ?? []).Select(a => (m.Component, Action: a)))
                .FirstOrDefault(x => ScopeKinds.Parse(x.Action.Scope) != ScopeKind.Cluster) is { Action: not null } narrow)
        {
            return $"'{narrow.Component}:{narrow.Action.Id}' declares scope '{narrow.Action.Scope}', and an application has one scope, cluster.";
        }

        return null;
    }

    /// <summary>
    /// The application whose manifests declare <paramref name="component"/>, or null when none does.
    /// </summary>
    public async Task<string?> OwnerOfAsync(string component, CancellationToken ct)
    {
        if (authority.Store is not { } store)
            return null;

        foreach (CatalogEntry entry in await store.CatalogAsync(ct).ConfigureAwait(false))
        {
            if (!ActionIds.TryParse(entry.Action.Id, out string declaredIn, out _) || declaredIn != component)
                continue;

            foreach (ActionDeclaration declaration in entry.DeclaredBy)
            {
                if (ApplicationNamespaces.ApplicationOf(declaration.Member) is { } owner)
                    return owner;
            }
        }

        return null;
    }

    private ManifestState Record(Application application, ManifestState state)
    {
        _states[application.Id] = state;
        if (state.Problem is { } problem)
            logger.LogWarning("the manifest of {Application} was not taken: {Problem}", application.Id, problem);
        return state;
    }
}

/// <summary>
/// Reads every application's manifest again on an interval, so an action an application starts declaring
/// arrives without anybody registering it again.
/// </summary>
/// <remarks>
/// Only while this anchor holds the accounts: a member standing by writes no catalog.
/// </remarks>
internal sealed class ApplicationManifestWorker(
    ClientRegistry clients,
    ApplicationCatalog catalog,
    AnchorRole role,
    AnchorOptions options,
    ILogger<ApplicationManifestWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.ManifestRefresh);
        try
        {
            do
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    /// <summary>Read every application's manifest once.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        if (!role.IsAuthority)
            return;

        foreach (Application application in clients.Applications.Where(a => a.ManifestUrl is not null))
        {
            try
            {
                await catalog.RefreshAsync(application, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "could not read the manifest of {Application}", application.Id);
            }
        }
    }
}
