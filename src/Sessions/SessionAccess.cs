using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace TheKrystalShip.Auth.Sessions;

/// <summary>
/// What a route needs: any one of its actions held by a person, or, for a service with ways in of its own,
/// any identity proved by one of <paramref name="OrSchemes"/>. An empty requirement is a route nobody is
/// admitted to.
/// </summary>
public sealed record ActionRequirement(IReadOnlyList<string> AnyOf, IReadOnlyList<string>? OrSchemes = null)
    : IAuthorizationRequirement;

/// <summary>Admits a request whose token lists one of the route's actions, or that one of its other schemes proved.</summary>
internal sealed class ActionRequirementHandler(IOptionsMonitor<SessionsOptions> options) : AuthorizationHandler<ActionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActionRequirement requirement)
    {
        var claim = options.CurrentValue.ActionsClaim;
        var met = context.User.Identities.Any(i => i.IsAuthenticated
            && (requirement.OrSchemes?.Contains(i.AuthenticationType) == true
                || requirement.AnyOf.Any(a => i.HasClaim(claim, a))));
        if (met) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>The policies a route names its requirement with.</summary>
public static class SessionAccess
{
    /// <summary>
    /// What a route that names no requirement answers when a service sets it as its fallback policy:
    /// nobody is admitted.
    /// </summary>
    public static AuthorizationPolicy Nobody { get; } = PolicyFor();

    /// <summary>The policy admitting a person whose token lists any one of <paramref name="anyOf"/>.</summary>
    public static AuthorizationPolicy PolicyFor(params string[] anyOf) => PolicyFor(anyOf, null);

    /// <summary>
    /// The policy admitting a person whose token lists any one of <paramref name="anyOf"/>, or any identity
    /// one of <paramref name="orSchemes"/> proves.
    /// </summary>
    public static AuthorizationPolicy PolicyFor(IReadOnlyList<string> anyOf, IReadOnlyList<string>? orSchemes)
    {
        string[] schemes = [JwtBearerDefaults.AuthenticationScheme, .. orSchemes ?? []];
        return new AuthorizationPolicyBuilder(schemes)
            .AddRequirements(new ActionRequirement(anyOf, orSchemes is { Count: > 0 } ? orSchemes : null))
            .Build();
    }

    /// <summary>The policy admitting any person with a valid token, whatever it lists.</summary>
    public static AuthorizationPolicy AnyPerson { get; } =
        new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build();

    /// <summary>Admits a person holding any one of <paramref name="anyOf"/>.</summary>
    public static TBuilder RequireAction<TBuilder>(this TBuilder builder, params string[] anyOf)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PolicyFor(anyOf));

    /// <summary>Admits any person with a valid token.</summary>
    public static TBuilder RequirePerson<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(AnyPerson);
}
