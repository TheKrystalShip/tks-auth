using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace TheKrystalShip.Auth.Sessions;

public static class SessionsServiceCollectionExtensions
{
    /// <summary>
    /// Admits people on the access tokens of an OpenID Connect provider: validated against the issuer's
    /// discovery and keys, its audience, ES256 and the access-token type, with the person and their actions
    /// read off the token. The options take <paramref name="defaults"/>, then the keys of the
    /// <paramref name="section"/> configuration section, then <paramref name="configure"/>. The JwtBearer
    /// scheme is the default authentication scheme, and its options stay open to <c>PostConfigure</c> for a
    /// service's own events.
    /// </summary>
    public static IServiceCollection AddSessions(
        this IServiceCollection services,
        Action<SessionsOptions>? configure = null,
        string section = SessionsOptions.Section,
        Action<SessionsOptions>? defaults = null)
    {
        var options = services.AddOptions<SessionsOptions>();
        if (defaults is not null) options.Configure(defaults);

        // Read key by key: the configuration binder reflects over the options type, which an AOT service cannot.
        options.Configure<IConfiguration>((o, configuration) =>
        {
            var keys = configuration.GetSection(section);
            o.Issuer = keys[nameof(SessionsOptions.Issuer)] ?? o.Issuer;
            o.Audience = keys[nameof(SessionsOptions.Audience)] ?? o.Audience;
            o.ActionsClaim = keys[nameof(SessionsOptions.ActionsClaim)] ?? o.ActionsClaim;
        });
        if (configure is not null) options.Configure(configure);
        options
            .Validate(o => Uri.TryCreate(o.Issuer, UriKind.Absolute, out var issuer)
                           && (issuer.Scheme == Uri.UriSchemeHttps || issuer.Scheme == Uri.UriSchemeHttp),
                $"{section}:Issuer must be the provider's issuer, an absolute https address.")
            .Validate(o => o.Audience.Length > 0, $"{section}:Audience must name the audience the service's tokens are minted for.")
            .Validate(o => o.ActionsClaim.Length > 0, $"{section}:ActionsClaim must name the claim listing a person's actions.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(_ => { });

        // Read when the options are first asked for, after every configuration source is in.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<SessionsOptions>>((jwt, sessions) => Configure(jwt, sessions.Value));

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, ActionRequirementHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, RefusalWriter>();
        services.AddSingleton<IStartupFilter, DeclaredActionsCheck>();
        return services;
    }

    private static void Configure(JwtBearerOptions jwt, SessionsOptions sessions)
    {
        jwt.Authority = sessions.Authority;
        jwt.RequireHttpsMetadata = sessions.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = sessions.Issuer,
            ValidAudience = sessions.Audience,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            // RFC 9068: an access token says it is one, which no id token does.
            ValidTypes = ["at+jwt"],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            // The token lives five minutes; a skew of the framework's own five would double that.
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            AuthenticationType = JwtBearerDefaults.AuthenticationScheme,
        };
        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is not ClaimsIdentity identity
                    || SessionPerson.From(identity, sessions.ActionsClaim, sessions.DeclaredActions) is not { } person)
                {
                    context.Fail("The token names nobody.");
                    return Task.CompletedTask;
                }
                person.AttachTo(context.HttpContext);
                sessions.Admitted?.Invoke(context.HttpContext, person);
                return Task.CompletedTask;
            },
        };
    }
}
