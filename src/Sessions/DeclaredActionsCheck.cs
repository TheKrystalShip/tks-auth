using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace TheKrystalShip.Auth.Sessions;

/// <summary>
/// Refuses to start a host whose routes name an action the service does not declare, so nothing is checked
/// that the manifest does not name. It runs once the pipeline is built, when every route is mapped.
/// </summary>
internal sealed class DeclaredActionsCheck(IOptions<SessionsOptions> options) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        if (options.Value.DeclaredActions is not { } declared) return;

        var strays = app.ApplicationServices.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(e => e.Metadata.OfType<AuthorizationPolicy>())
            .SelectMany(p => p.Requirements.OfType<ActionRequirement>())
            .SelectMany(r => r.AnyOf)
            .Where(a => !declared.Contains(a))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (strays.Count > 0)
            throw new InvalidOperationException($"A route checks {string.Join(", ", strays)}, which the service does not declare.");
    };
}
