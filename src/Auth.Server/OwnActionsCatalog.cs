using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Auth.Server;

/// <summary>
/// Enters the provider's own actions into its catalog.
/// </summary>
/// <remarks>
/// An action is grantable only once the catalog holds it. The provider's report is built from
/// <see cref="AuthActions.Declared"/> and handed to <see cref="AuthorityIntake"/> on every start. An
/// unchanged report changes nothing and journals nothing.
/// </remarks>
internal sealed class OwnActionsCatalog(AuthorityIntake intake, ILogger<OwnActionsCatalog> logger) : IHostedService
{
    private static readonly string Version =
        typeof(OwnActionsCatalog).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>The provider's report: one manifest, its own.</summary>
    internal static CatalogReport Report(DateTimeOffset now) => new(
        Manifests:
        [
            new ActionManifest(
                ActionManifest.SupportedSchemaVersion,
                ActionIds.AuthComponent,
                Version,
                [
                    .. AuthActions.Declared.Select(a => new ManifestAction(
                        a.Id[(ActionIds.AuthComponent.Length + 1)..],
                        a.Title,
                        ActionEffects.ToWire(a.Effect),
                        a.Self ? true : null)),
                ]),
        ],
        Sequence: now.ToUnixTimeMilliseconds());

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await intake.DeclaredAsync(ApplicationNamespaces.ProviderSource, Report(DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The daemon still serves; what is missing is that nobody but an Owner can be granted the
            // actions that administer access, which is what this line says.
            logger.LogError(e, "could not enter the provider's own actions into its catalog");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
