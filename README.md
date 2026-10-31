# tks-auth

An OpenID Connect provider: it holds the **accounts**, the **applications** that sign people in through it
and each application's **roles**, and signs people in. An application checks a token against the
provider's published keys and reads the actions the person holds in it from the token's `tks_actions`
claim; nothing about a person is copied to the application.

One daemon, **`tks-auth`**, and the libraries it is built from by project reference. Nothing is published
to NuGet: minting, the session registry, the Discord round trip and the OAuth handshake are the daemon's
own code.

## Layout

| project | contents |
|---|---|
| `src/Auth` | the identity (`AccountIdentity`), the failure of an identity or account source, the token claim names, the `provider:name` actor convention. No dependencies, AOT-safe. |
| `src/Auth.Access` | the access model: actions, permissions, ranked roles and the assignments that give a person a role; the evaluator every access question goes through (`AccessEvaluator.Allows`); the rules deciding who may change any of it (`AuthorityRules`); and what `/me/access` answers (`AccessReport`). No dependencies, AOT-safe. |
| `src/Auth.Users` | the account store: local passwords and the credentials that prove an account, and the authority over them, in one SQLite file (`SqliteAuthorityStore`). |
| `src/Auth.Journal` | the audit events: their names and the payloads written for each. |
| `src/Auth.Server` | the `tks-auth` daemon. |
| `web/` | the sign-in, wait, account, device and admin pages (Vite + React on krystal-ui), built into `ui/` beside the binary. |
| `client/` | `@thekrystalship/auth-client`, the browser sign-in package a site configures. |

The daemon ships as a pacman package and a systemd unit, running as the `tks-auth` system user, with its
pages built into `ui/` beside the binary.

## The model

Applications declare the **actions** they perform (`cinema:watch`, `auth:roles.edit`); actions are filed
into **permissions**, permissions into ranked **roles**, and a role is **assigned** to an account. Owner
holds everything; `everyone` is what every active person holds. A role may hold every action of an
application, those declared later included.

One function decides every request:

```csharp
AccessDecision decision = evaluator.Allows(accountId, "auth:roles.edit");
if (!decision.Allowed)
    return Deny(decision.Reason);
```

A token proves who and, for an application, lists the application's actions the account holds, evaluated by
the same function when the token is minted (see [Applications](#applications)). A store that cannot be read
**throws** rather than answering: "we could not ask" is not "the answer is no", and reporting the first as
the second locks an Owner out mid-incident.

## Configuration

The daemon is configured by the `Auth` section of `tks-auth.settings.json`, which declares every key with
its default; an environment variable overrides one by spelling its path with `__`
(`Auth__ListenAddress`). A variable naming a key the file does not declare binds to nothing. Operator
overrides live in `/etc/tks-auth/tks-auth.env`; the issuer is that file's alone.

| key | meaning |
|---|---|
| `Auth__Issuer` | the provider's one address, the `iss` of every token |
| `Auth__ListenAddress` | where Kestrel listens |
| `Auth__UserStorePath`, `Auth__SessionStorePath`, `Auth__SigningKeyPath` | the account store, the session registry and the signing key |
| `Auth__UiPath` | where the pages are installed |
| `Auth__AccessLifetimeMinutes`, `Auth__RefreshLifetimeDays` | how long a bearer lasts, and how long somebody stays signed in |
| `Auth__AllowSelfRegistration`, `Auth__PendingCap`, `Auth__PendingTtlDays` | who may make an account, and how many may wait for approval |
| `Auth__ReauthWindowMinutes` | how long a proved credential lets somebody change how they sign in |
| `Auth__SessionCleanupMinutes` | how often dead session rows are swept |
| `Auth__ManifestRefreshMinutes` | how often every application's manifest is read again |
| `Auth__DiscordTokenCacheMinutes` | how long what Discord says about a token is remembered |

The external providers it offers come from `Auth:Providers`, keyed by provider name
(`/etc/tks-auth/providers.env`):

```
Auth__Providers__discord__ClientId=…        # one OAuth application, at one provider
Auth__Providers__discord__ClientSecret=…    # environment only
Auth__Providers__github__ClientId=…         # a second provider is two more keys
Auth__Providers__github__ClientSecret=…
```

A provider with both keys set is offered on the sign-in page; one with either missing, and one nobody
has heard of, are the same answer — not offered.

## Development

```bash
dotnet build tks-auth.slnx
dotnet test tks-auth.slnx
dotnet publish src/Auth.Server/Server.csproj -c Release -r linux-x64 -o artifacts/publish   # 0 ILC warnings
(cd web && npm ci && npm run build && npm run check && npm run lint)
```

## An external provider's round trip

When the provider sends a browser to Discord, one handshake carries both halves of the defence, in one
HttpOnly cookie:

```csharp
// /auth/start
var handshake = OAuthHandshake.Create();
Response.Cookies.Append("tks_auth_oauth_state", handshake.ToCookieValue(), new CookieOptions
{
    HttpOnly = true,
    Secure = Request.IsHttps,
    SameSite = SameSiteMode.Lax,   // NOT Strict — Strict suppresses the cookie on the way back
    Path = "/auth",
});
return Redirect(directory.BuildAuthorizeUrl(handshake.State, handshake.CodeChallenge, "none"));

// /auth/callback
if (!OAuthHandshake.TryParse(Request.Cookies["tks_auth_oauth_state"], out var handshake)
    || !handshake.MatchesState(state))
    return BadRequest();   // forged, expired, or another browser's login

AccountIdentity? identity = await directory.VerifyAsync(code, handshake.CodeVerifier, ct);
```

**`state` and PKCE are not alternatives.** `state` stops login CSRF — without it an attacker starts
their own login, sends the victim a callback link carrying the attacker's `code`, and the victim's
browser is handed a session for the *attacker's* identity. PKCE stops code interception, because a
`code` rides back in a URL and URLs leak.

**The state has to be bound to the browser, and only the cookie binds it.** Checking a returned state
against a server-side set of issued states proves that *some* login started here — which is
true of the attacker's login too, so it admits the exact request it was meant to refuse. Single-use
consumption stops replay, not CSRF.

Carrying the verifier in the same cookie is what lets the provider run PKCE with **no server-side
pending store**, so a round trip survives a restart.

## Honest failure

A login answers three different things and they must not be collapsed:

| what happened | means | result |
|---|---|---|
| the code exchanged, `users/@me` answered | a verified identity | the account it proves, or an unapproved one created for it |
| `4xx` from the token endpoint | an expired, replayed or forged code | `null` — a `401`, start again |
| `5xx`, unreachable, malformed | **unknown** | `DiscordAuthException` — a `502` |

A token exchange asks Discord the same kind of question and keeps the same three answers: `oauth2/@me`
naming a user is an identity, a `401`/`403` is `invalid_grant`, and a `429`, a `5xx` or no answer is
`temporarily_unavailable` with a `502`.

The third is the one that matters. "We could not ask" is not "the answer is no": a door that reads
an outage as a verdict either locks out someone who really does hold the role or admits someone who
does not. The same rule holds one layer down — a store that cannot be read throws rather than
resolving to `none`.

## Sessions

The provider mints every session. A stateless JWT can say who someone is; it cannot say whether their
session is still alive, and that one fact is what the session registry holds.

```csharp
var tokens = new SessionTokenService(
    new SessionTokenOptions(
        BearerAudience: "tks-auth",
        AccessLifetime: TimeSpan.FromMinutes(15),
        RefreshLifetime: TimeSpan.FromDays(30),
        Issuer: "https://auth.example.com"),
    signer);

MintedToken access  = tokens.MintApplicationAccess(access);
MintedToken refresh = tokens.MintApplicationRefresh(identity, sid);
```

**`RefreshLifetime` is written once and used twice** — the token's expiry and the registry row's. It
is a setting rather than a constant precisely so there is no second copy to drift, because the drift
is invisible until a token outlives its own row or the reverse.

**The audience and the issuer are validated everywhere.** Changing the issuer makes every application
refuse every token already issued, and everybody signs in again.

**An application verifies and never mints.** It checks ES256 against the published key set, with the
algorithm pinned so the public key can never be offered back as an HMAC secret.

### Rotation and reuse

Every refresh mints a new pair and stores the new `jti`. A refresh presenting any other `jti` is a
replay of a token that has already been rotated away — `RotateAsync` returns false, and the caller
refuses. It cannot tell a stale client from a stolen token, so it refuses both and lets the real
holder authenticate again.

### The cache is the revocation bound

The `SessionValidator` caches the registry's answer, so the hot path does not query per request. A revoke
evicts, making the kill immediate; the TTL is the backstop for what cannot evict. Expiration is
**absolute, never sliding** — a sliding window is extended by every hit, so the busiest session, the
one most worth revoking, would be the one that never re-checks. A "no" is cached too, because a
revoked session still presenting its token is exactly what a stolen one does.

## The provider

One store, one writer, one address a person signs in at — an OpenID Connect provider every browser surface
is a client of. The design is `kgsm-docs/systems/identity/`; this is what the daemon serves.

| | |
|---|---|
| `GET /health` | the liveness probe |
| `GET /authorize`, `POST /token`, `POST /revoke`, `GET /sign-out` | the OpenID Connect doors; the only way a session is minted, renewed or ended by its holder |
| `GET /.well-known/openid-configuration`, `GET /.well-known/jwks.json` | discovery, and the verification key set |
| `GET /auth/admin/users` | every account, to a caller holding any `auth:*` action |
| `GET /auth/admin/users/{id}/sessions` | an account's live sessions, on `auth:accounts.disable` |
| `GET /auth/admin/authority`, `POST /auth/admin/authority/edits`, `POST /auth/admin/authority/checks` | who may do what, and the rules' answer to a change |
| `GET /auth/admin/operations` | every gated route and the action it requires |
| `/auth/admin/applications` | the applications, their clients and secrets, on `auth:applications.manage` |
| `GET /me/access` | the caller's own `auth:*` actions |

**Sessions are signed asymmetrically.** The provider holds the private key; applications verify with the
published public one.

```
GET /.well-known/jwks.json
{"keys":[{"kty":"EC","crv":"P-256","x":"…","y":"…","alg":"ES256","use":"sig","kid":"…"}]}
```

The key is a set so rotation has somewhere to go: publish the incoming key beside the outgoing one, let
every verifier pick up both, then move the signer. `kid` is the key's own RFC 7638 thumbprint.

**The private key is generated once and never replaced by accident.** It lives at
`/var/lib/tks-auth/session-signing.pem`, `0600`, created with that mode rather than chmod'd
after — the gap between the two is the window the whole mode exists to close. A file that exists and
cannot be read stops the daemon: replacing it invalidates every session and leaves every application
checking against a key nothing signs with, so "no key yet" and "the key is unreadable" must not take the
same path.

**An application's access is evaluated at every mint, never read off an old token.** The account's
standing is re-read on refresh too, and a withdrawn account has its session ended there rather than being
left to run out its bearer.

### The OpenID Connect provider

The provider speaks authorization code with PKCE (S256), public and confidential clients, no consent
screen, at its issuer. `Auth__Issuer` is the provider's one address, set by the deployment in
`/etc/tks-auth/tks-auth.env` (`https://auth.example.com`): browsers sign in at it and every provider
callback is built on it. With anything but a URL
every door below answers `no_issuer` and mints nothing. An https issuer is served by `tks-auth-serve`, which
issues the name's certificate and renders its vhost. The design is `kgsm-docs/systems/identity/sign-in.md`, the wire
`kgsm-docs/reference/oidc-provider.md`.

| | |
|---|---|
| `GET /.well-known/openid-configuration` | discovery |
| `GET /.well-known/jwks.json` | the key set every session and `id_token` is signed with |
| `GET /authorize` | validate and hold the request, then answer a recognised browser or show the sign-in page |
| `POST /authorize/credentials` | a password against the request in flight, same-origin only; a code, never a session |
| `GET /authorize/wait` | an account awaiting approval, polled by a refresh or, with `Accept: application/json`, by the page |
| `GET /authorize/context` | what the pages draw for the request in flight: the client, the providers, whether registration is open |
| `POST /authorize/register` | make an account against the request in flight, same-origin only; the wait follows |
| `GET /authorize/{provider}` | an external provider's round trip for the request in flight |
| `POST /token` | `authorization_code` or `refresh_token`; access, refresh and `id_token`. A confidential client authenticates by `client_secret_basic` or `client_secret_post`, and may exchange a Discord credential for an access token (see [Exchanging a Discord credential](#exchanging-a-discord-credential)) |
| `POST /revoke` | end the session a refresh token belongs to (RFC 7009), on behalf of the client it was minted for; `200` for any token that ends nothing (see [Signing a device out](#signing-a-device-out)) |
| `GET /userinfo` | the account behind a bearer issued for the provider's own audience (`tks-auth`); an application's access token is that application's alone and is refused |
| `GET /sign-out`, `POST /sign-out` | end a browser's sign-in and everything minted under it; asks without a hint |
| `/auth/admin/applications` | the applications and their clients and secrets, on `auth:applications.manage` (see [Applications](#applications)) |

**Two cookies on the provider's own origin, both `HttpOnly; SameSite=Lax; Path=/`.** `tks_auth_authz` names
the request in flight, held here for ten minutes so the page and its form carry no request field.
`tks_auth_session` names the browser's provider session, a row in the session registry beside the sessions
minted under it; a second surface comes back signed in on the strength of it. Each holds a random
secret and the row is found by its hash.

**Every session minted through the provider records its provider session**, so signing out ends all of
them, a second account on the same browser ends the first's, and the same account proving itself again
keeps them. The sessions page lists the provider session with `kind: provider`; ending it there is
signing out.

**Every client belongs to an application,** and a client with none signs nobody in. An application has the
clients registered with it, by the Applications admin page or `tks-auth app client add`. tks-auth's own
admin pages are the one declared client: public, built in, redirecting to `/admin/` on the issuer's own
origin. Redirects are matched exactly and must be HTTPS, or HTTP on this machine, a private network or a
local name. A registered client's origin may read discovery, the key set, `/token`, `/revoke`,
`/device_authorization`, `/userinfo` and the account API across origins, without credentials; nothing that
reads the provider's cookie answers another origin.

**The floor.** The sign-in page is a plain document with a working form and the provider links, under a
content security policy with no inline script or style and `form-action` naming the one client the
request returns to. It signs somebody in with scripting off.

### The pages, the account page and the admin pages

The pages people see are this repo's `web/`, shipped in the package beside the binary and read from
`Auth__UiPath` (`/opt/tks-auth/ui`) on every request, their assets served at `/ui/`. Each is a
document with its own floor — the sign-in form, a wait that refreshes only with scripting off — which
the application replaces on mount; the provider fills in the provider links and nothing else. Absent,
the provider renders the floor itself and says the pages are not installed, and a plain form post that
fails is always answered on that built-in page with the reason.

**The admin pages** are `/admin/`: accounts and their approval, roles, permissions, the catalog grouped
by application, assignments and applications. They are an OpenID Connect client of
this provider — the built-in public client `tks-auth`, redirecting to `/admin/` on the issuer's own
origin — and call the admin routes below with a token for tks-auth's own application (audience
`tks-auth`), which those routes accept and no other application's token is.

| | |
|---|---|
| `GET /` | a redirect to the account page |
| `GET /account` | the account page, or its sign-in when this browser holds none |
| `GET /account/sign-in` | sign in at the provider to reach the account page; no code is minted |
| `GET /account/me` | the account, its identities, its sessions and until when the last proof is recent |
| `POST /account/reauth` | the password again; `GET /account/reauth/{provider}` for an account with none |
| `POST /account/password` | set its own password |
| `POST /account/identities/{provider}/start`, `DELETE /account/identities/{id}` | attach or detach an identity |
| `POST /account/sessions/revoke`, `POST /account/sign-out` | end a session, all of them, or this browser's sign-in |
| `POST /account/sessions/name` | rename a device signed in to the account, `{sid, name}`; empty takes the name away, and anything not a device is `not_a_device` |

Every call is authenticated by `tks_auth_session` and every write is same-origin only. **Changing how
somebody signs in needs a recent proof** — the provider session's last credential inside
`Auth__ReauthWindowMinutes` — and the page asks for the password (or a round trip to a provider the
account already holds) when it is older. A provider round trip for that purpose accepts only an identity
already attached to the same account; it never signs anybody in and never makes an account.

**A client's origin is what is admitted across origins, and nothing else.** A surface on another
origin reads the discovery document, exchanges its code and drives the administration with the bearer
it holds, so every registered client's origin is answered on those paths — without credentials, and
never on anything that reads the provider's cookie. There is deliberately no wildcard: this surface
mints credentials.

### Applications

An **application** is an API this provider signs people in to: an id, which is also the namespace its
actions are declared in (`cinema` declares `cinema:*`), a name, an **audience**, the address it serves
its action manifest at, an access lifetime, the Discord applications its clients may present, whether
it may act for linked Discord users, and the clients it signs people in through. **One is built in:**
tks-auth itself (`auth`), whose audience is `tks-auth`, whose one client is the admin pages', and whose
tokens list the `auth:*` actions the account holds. Every other application is registered on `auth:applications.manage`, by the Applications admin page or on
the host, and is stored beside the clients in the session store.

```bash
tks-auth app add cinema --name "Krystal Cinema" --manifest https://movies.example.com/.well-known/tks-actions.json \
    --client-id cinema-site --redirect https://movies.example.com/signin
tks-auth app client add cinema --client-id cinema-api --confidential     # the secret is printed once
tks-auth app set cinema --lifetime 5
tks-auth app rotate-secret cinema cinema-api
tks-auth app adopt cinema              # every client with no application becomes a client of cinema
tks-auth app list
tks-auth app remove cinema
```

The command and `/auth/admin/applications` (`GET`/`POST`, `PATCH`/`DELETE …/{id}`,
`POST …/{id}/clients`, `DELETE …/{id}/clients/{client}`, `POST …/{id}/clients/{client}/secret`) are one
code path, `ApplicationRegistry`, journaled as `auth.application.*`. The command runs as the provider's
service account over the daemon's own files, and the daemon picks the change up within a second.

**An application's token is its own.** Its access token is audienced to the application, typed
`at+jwt`, names the account in `sub` exactly as the `id_token` does, carries `client_id`, and lives the
application's lifetime — five minutes unless set. It lists the application's actions the account holds in
**`tks_actions`**, an array, evaluated by `AccessEvaluator` at every mint and every refresh, so a role taken away reaches the application within one
lifetime. Its refresh token is audienced to the issuer and is accepted nowhere but `/token`. Every resource
server refuses a token that is not audienced to it.

**Confidential clients authenticate with a secret** the provider makes, shows once and keeps as a hash:
`client_secret_basic` or `client_secret_post`, one at a time, and a wrong one is `invalid_client` with
`401`. They are held to PKCE like every client, and only the client a session was minted for continues
it.

**The catalog pulls each application's manifest** (`kgsm-docs/reference/action-manifest.md`) from its address
when it is registered, when the address changes and every `Auth__ManifestRefreshMinutes`. Its actions
are held under `application:<id>` beside the provider's own and arrive unmapped. The document is one
manifest in the application's own namespace, or a `manifests` list with one per component for an application
whose actions span several namespaces; the application then owns its id and each of those components, its
tokens list their actions, and a role that holds every action of it holds them all. A manifest in another
namespace (an unlisted one, `auth` or another application's) is refused whole, and one that cannot be read
changes nothing.

### Exchanging a Discord credential

An application whose surfaces live in Discord gets its tokens by **token exchange** (RFC 8693) at
`/token`, from one of its confidential clients — never from a public one:

```http
POST /token
Authorization: Basic <client id:secret>
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:token-exchange
&subject_token=<subject>
&subject_token_type=<one of the two below>
```

| `subject_token_type` | `subject_token` | the client's application must | the token |
|---|---|---|---|
| `urn:tks:params:oauth:token-type:discord-access-token` | a Discord access token with `identify` — an Activity's | list the Discord application it was issued to (`--discord-app`) | names the person |
| `urn:tks:params:oauth:token-type:discord-user-id` | a Discord user id — whoever invoked a bot | act for Discord users (`--act-for-discord`) | names the person, with `act: {"sub": "<client id>"}` |

For a Discord token, the provider asks Discord's `oauth2/@me` whose it is and which Discord application
holds it; it never holds that application's secret. The Discord identity resolves to the account it is
attached to, and an active account gets the application's access token exactly as a sign-in would mint it
— `at+jwt`, its audience, its lifetime, `tks_actions` — answered as RFC 8693 §2.2.1 says:

```json
{ "access_token": "…", "issued_token_type": "urn:ietf:params:oauth:token-type:access_token",
  "token_type": "Bearer", "expires_in": 300 }
```

There is no refresh token and no session: the holder exchanges again before it runs out. `audience` may
name the application's own and `requested_token_type` the access-token type; a `resource`, a `scope`, an
actor token or anything else asked for is refused (`invalid_target`, `invalid_scope`, `invalid_request`).

**An account that is not active is told why, never given a token.** The answer is `invalid_grant` with
the extension parameter `account_status`:

| `account_status` | means |
|---|---|
| `pending` | the account waits on an administrator's approval |
| `disabled` | the account is switched off |
| `unknown` | no account holds the Discord identity |

A Discord token whose identity nobody holds makes a **pending** account, under the same rule and cap a
Discord sign-in here does. A Discord id alone makes nothing — learning who an id is would take a bot
token — so it is `unknown` until the person signs in or links the identity.

**What Discord says about a token is remembered for the token's life, at most
`Auth__DiscordTokenCacheMinutes` (10)**, under the token's SHA-256, so an Activity re-exchanging every
few minutes asks Discord once. A refusal or an outage is never remembered. A token revoked at Discord is
still exchanged for up to that cap.

Every exchange an authenticated client asks for is journaled — `auth.token.exchanged`, or
`auth.token.exchange_refused` with its reason — with the client, the application, the Discord identity,
the account and the acting client.

### Signing a device in

A screen with no browser to bounce — a television — signs in with the **device authorization grant**
(RFC 8628), as a public client of an application registered for it:

```bash
tks-auth app client add cinema --client-id cinema-tv --device
```

The device asks for its codes, naming its client and, when it has one, the name its owner gave it:

```http
POST /device_authorization
Content-Type: application/x-www-form-urlencoded

client_id=cinema-tv&scope=openid&device_name=Living%20room%20TV
```

```json
{ "device_code": "…", "user_code": "BKMR-QTFX",
  "verification_uri": "https://auth.example/device",
  "verification_uri_complete": "https://auth.example/device?user_code=BKMR-QTFX",
  "expires_in": 600, "interval": 5 }
```

It shows the user code, and `verification_uri_complete` as a QR code, then asks `/token` every `interval`
seconds with `grant_type=urn:ietf:params:oauth:grant-type:device_code`, its `device_code` and its
`client_id`. Until somebody decides the answer is `authorization_pending`; asking sooner than the interval
is `slow_down` and adds five seconds to it; a refusal is `access_denied` and a code nobody decided in ten
minutes `expired_token`, each said once. An approval answers with an access token for the application and
a refresh token, the code flow's pair without an `id_token`.

The person opens `/device` on a phone or a computer, signs in if that browser is not signed in here, and
is shown the application, their account, the code and what the device said it is before the two buttons,
with the device's name in a field they may change. The name left there, cleaned and cut to 64 characters,
is the device session's: listed with it and carried on its access tokens as `tks_device_name`.
Only an active account approves, and the account is read again when the device collects. A browser that
keeps entering codes that name no device is made to wait.

**The device's session is a sign-in of its own.** It is listed on the account page as a device, renews
with its refresh token like any other, and ends when it is ended there or the account is disabled — not
when the browser that approved it signs out.

### Signing a device in

A screen with no browser to bounce — a television — signs in with the **device authorization grant**
(RFC 8628), as a public client of an application registered for it:

```bash
tks-auth app client add cinema --client-id cinema-tv --device
```

The device asks for its codes, naming its client and, when it has one, the name its owner gave it:

```http
POST /device_authorization
Content-Type: application/x-www-form-urlencoded

client_id=cinema-tv&scope=openid&device_name=Living%20room%20TV
```

```json
{ "device_code": "…", "user_code": "BKMR-QTFX",
  "verification_uri": "https://auth.example/device",
  "verification_uri_complete": "https://auth.example/device?user_code=BKMR-QTFX",
  "expires_in": 600, "interval": 5 }
```

It shows the user code, and `verification_uri_complete` as a QR code, then asks `/token` every `interval`
seconds with `grant_type=urn:ietf:params:oauth:grant-type:device_code`, its `device_code` and its
`client_id`. Until somebody decides the answer is `authorization_pending`; asking sooner than the interval
is `slow_down` and adds five seconds to it; a refusal is `access_denied` and a code nobody decided in ten
minutes `expired_token`, each said once. An approval answers with an access token for the application and
a refresh token, the code flow's pair without an `id_token`.

The person opens `/device` on a phone or a computer, signs in if that browser is not signed in here, and
is shown the application, their account, the code and what the device said it is before the two buttons,
with the device's name in a field they may change. The name left there, cleaned and cut to 64 characters,
is the device session's: listed with it and carried on its access tokens as `tks_device_name`.
Only an active account approves, and the account is read again when the device collects. A browser that
keeps entering codes that name no device is made to wait.

**The device's session is a sign-in of its own.** It is listed on the account page as a device, renews
with its refresh token like any other, and ends when it is ended there or the account is disabled — not
when the browser that approved it signs out.

### Signing a device out

A device has no `id_token` and no browser sign-in for `/sign-out` to end, so it signs out by **revoking
its refresh token** (RFC 7009) at the `revocation_endpoint` discovery names — what an OIDC client
library's revocation call sends:

```http
POST /revoke
Content-Type: application/x-www-form-urlencoded

token=<refresh token>&token_type_hint=refresh_token&client_id=cinema-tv
```

The answer is `200` with no body. The session that refresh token belongs to ends, and the line is `auth.session.revoked` with scope `self`. Any client ends its own sessions this
way; a session minted under a browser's sign-in ends alone and the browser stays signed in.

- **Only the client the session was minted for.** A public client names its `client_id`; a confidential
  one authenticates as at `/token`, and without its secret is `invalid_client`. A token presented by, or
  naming, another client is `unauthorized_client` and ends nothing.
- **Nothing is learned from the answer.** A token that does not verify, has expired, or belongs to a
  session already ended is `200`, the same as one that was revoked. An access token is `200` and ends
  nothing: it is a bearer resource servers hold, and it lives minutes. The hint is not read.

### Running one

```bash
./deploy/setup.sh          # once per host, asks for sudo
./deploy/deploy.sh         # every deploy, no sudo, no prompts — the binary and the pages (needs node)
```

A checkout deploy runs the unit as the deploying user. The pacman package installs it **switched off**,
as the `tks-auth` system user, with its state in `/var/lib/tks-auth`; enable it once `Auth__Issuer` is set.

## Accounts

**The provider owns users.** An account exists on its own — a local password is enough to sign in, with no
external provider configured at all. A Discord, GitHub or Google identity is one *credential attached
to* an account, never the source of one, and never a source of authority.

```csharp
var store  = new SqliteAuthorityStore(new UserStoreOptions());     // the account store
var signIn = new LocalSignInService(store, new IdentityPasswordHasher());

LocalSignInResult result = await signIn.SignInAsync(username, password, DateTimeOffset.UtcNow);
if (result.Outcome == LocalSignInOutcome.Success)
    Mint(result.Identity!);
```

**A credential answers "which account is this"; the account's roles answer "what may they do".**
Nothing else contributes. That is what lets a provider be added with no access story of its own — the
login path is the same shape whichever credential proved the account.

**One writer.** The provider is the only process that writes the account store, and every request is
evaluated from it, so a change of access lands on the next request.

### The schema rule

Wiping this database is every account, every password and every link, with nothing to re-derive them
from, so the ecosystem's `EnsureCreated`-and-wipe rule does not apply. A schema change is a migration
that runs once, inside a transaction, when the file is opened: it keeps every account, credential, role,
permission and assignment, and a copy of the file as it was is written beside it first
(`accounts.db.pre-<version>`). A file at a version this build neither reads nor upgrades from is
**refused outright** rather than half read.

Opening a file written by schema version 2 upgrades it once: the replication tables, the service accounts
and their requirements, and every assignment at a node or an instance are dropped (each assignment is
logged with its account, role and scope, so it can be granted again), and the actions members of a KGSM
cluster had reported stay in the catalog as the report of the application `kgsm`, until that application's
own manifest replaces it.

### What a password costs

- **An unknown username and a wrong password are one outcome at one cost.** Distinguishable answers
  are a username oracle, and so is a faster one — an unmatched username still spends a hash
  verification against a decoy.
- **Lockout is exponential from a threshold, not a hard cap.** A hard cap hands anyone who knows a
  username a denial of service against its owner; doubling delays cost an attacker orders of
  magnitude within a handful of attempts.
- **The file is `0600`, and `-wal`/`-shm` with it.** SQLite gives those two the mode the database had
  when it created them, so the store sets the mode *before* enabling WAL.
- **It lives in a directory the service user owns** (`/var/lib/tks-auth`). SQLite writes `-wal` and
  `-shm` *beside* the database, so WAL needs write permission on the **directory**, not just the file.
- **No SMTP, anywhere.** A reset is set by somebody holding `auth:accounts.create`.
