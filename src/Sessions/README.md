# TheKrystalShip.Auth.Sessions

How a service admits a person signed in at tks-auth. The service signs nobody in: it reads the provider's
discovery and keys, checks each bearer access token it is handed, and takes what the person may do from the
token's actions claim. Every KGSM component that admits people, Cinema and Galley use it.

A token is admitted when it is signed with ES256 by a key in the issuer's key set, carries the issuer and the
service's audience, says it is an access token (`typ` `at+jwt`, RFC 9068) and has not expired (30 seconds of
skew). The token contract is `kgsm-docs/reference/session-tokens.md`.

## Wiring

```csharp
builder.Services.AddSessions(
    section: "Oidc",                       // where Issuer, Audience and ActionsClaim are read; "Sessions" by default
    defaults: o =>
    {
        o.Audience = "galley";             // a section key overrides a default
        o.DeclaredActions = GalleyActions.All.Select(a => a.Id).ToHashSet();
        o.WriteRefusal = (http, refusal) => http.Response.WriteAsJsonAsync(...);
        o.Admitted = (http, person) => ...;
    });
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(SessionAccess.Nobody);   // every route names what it needs
```

- `configure` runs after the section is read, for values the service holds itself.
- `DeclaredActions`, when set, makes the host refuse to start while a route checks an action outside it, and
  a person holds only those actions.
- `WriteRefusal` writes the body of a 401 or a 403 in the service's error shape; the status and the
  `WWW-Authenticate: Bearer` challenge are already set. Unset, the body is `{ "error": { "code", "message" } }`.
- `Admitted` runs once per request whose token checks out, with the person read from it.
- The JwtBearer options stay open to `PostConfigure` for a service's own events, such as a hub reading its
  token from the query string.

## Routes

```csharp
app.MapGet("/plans", ...).RequireAction(GalleyActions.ProfileRead);   // a person holding any one of these
app.MapGet("/me", ...).RequirePerson();                                // any person with a valid token
app.MapGet("/media/{id}", ...).RequireAuthorization(
    SessionAccess.PolicyFor([CinemaActions.Watch], [CinemaSchemes.MediaTicket]));   // or another scheme's identity
```

`SessionPerson.Of(httpContext)` is the person a request acts as: `Subject`, `DisplayName`, `Actions`,
`ExpiresAt`, and `Via`, the client acting for them when the token carries an `act` claim.
`SessionPerson.UntilExpiry(httpContext)` gives a cancellation source that ends a stream when the token does.

## Tests

`TheKrystalShip.Auth.Sessions.Testing` holds `TestIssuer`: discovery, a key set, a token endpoint whose
exchanges a test answers, and `Mint(...)` for tokens as the provider mints them. `UseTestIssuer(services)`
points the host's backchannel at it.
