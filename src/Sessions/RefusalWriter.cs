using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

namespace TheKrystalShip.Auth.Sessions;

/// <summary>The error shape this package writes when the service names none: <c>{ "error": { "code", "message" } }</c>.</summary>
internal sealed record RefusalEnvelope(RefusalBody Error);

internal sealed record RefusalBody(string Code, string Message);

/// <summary>The <c>act</c> claim, of which only the acting client's id is read.</summary>
internal sealed record ActorClaim(string? Sub);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RefusalEnvelope))]
[JsonSerializable(typeof(ActorClaim))]
internal sealed partial class SessionsJsonContext : JsonSerializerContext;

/// <summary>
/// How a refusal is answered: a 401 with <c>WWW-Authenticate: Bearer</c> for no usable token, a 403 for
/// a person who may not do this, each with the body the service writes.
/// </summary>
internal sealed class RefusalWriter(IOptionsMonitor<SessionsOptions> options) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _admitted = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        // A request no route matched is nobody's to refuse: what answers it is the 404 it would have had.
        if (context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        if (result.Challenged)
        {
            await context.ChallengeAsync(JwtBearerDefaults.AuthenticationScheme);
            await Write(context, Refusal.SignIn);
            return;
        }

        if (result.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await Write(context, Refusal.NotAllowed);
            return;
        }

        await _admitted.HandleAsync(next, context, policy, result);
    }

    private Task Write(HttpContext context, Refusal refusal) =>
        options.CurrentValue.WriteRefusal is { } write
            ? write(context, refusal)
            : context.Response.WriteAsJsonAsync(
                refusal == Refusal.SignIn
                    ? new RefusalEnvelope(new RefusalBody("unauthorized", "Sign in to continue."))
                    : new RefusalEnvelope(new RefusalBody("forbidden", "Your account does not let you do that.")),
                SessionsJsonContext.Default.RefusalEnvelope);
}
