# `Auth.Server` (`tks-auth`) — locked decisions

The OpenID Connect provider that holds the accounts and signs people in, and the applications they sign in
to. Built from this repo's libraries by project reference. Canon: `kgsm-docs/systems/identity/`
(`sign-in.md`, `sessions.md`, `applications.md`), `kgsm-docs/reference/oidc-provider.md` and
`session-tokens.md`; this repo's `README.md` documents the doors. Minting is `Minting/CLAUDE.md`.

## The daemon

- **`Program.cs` composes `TksAuthCore`** (`AddTksAuthCore`, `MapTksAuthCore`): accounts, sessions, the
  OpenID Connect doors, the applications and their clients, the account page, the admin and authority
  routes and the audit log. It reads nothing outside its own settings, its state directory and
  `/etc/tks-auth`.
- **Configuration is the `Auth` section of `tks-auth.settings.json`,** bound to `ServerSettings` and
  validated into `ServerOptions`. A key the file does not declare binds to nothing.
- **Every sign-in mints an application's token.** A client belongs to an application, a client with none
  signs nobody in, and the access token carries that application's audience, lifetime and `tks_actions`.
- **Signing is asymmetric, `ValidAlgorithms` is pinned, and the private key is generated exactly once.** A
  file that exists and cannot be read stops the daemon and is never a reason to generate. It is created
  with mode `0600`, not chmod'd after.
- **The daemon runs on the account store,** `SqliteAuthorityStore`, which is both the authority and the
  `IUserStore` every door reads. The file is `/var/lib/tks-auth/accounts.db`. Opening a file written by the
  previous schema upgrades it in place after a `.pre-<version>` copy, and logs what it removed.
- **A session proves who, never what.** Every route decides its caller by an action,
  `Endpoints.RequireCaller(ctx, action)`, evaluated against the authority and, for `auth:*`, held to a
  recent sign-in. The one bearer accepted at this daemon's own doors is a session of tks-auth's own
  application audienced to `tks-auth` (`SessionReader`); any other application's token is that
  application's alone.
- **A gated route declares its action on itself, and that declaration is both enforced and published.**
  `AuthAction` metadata on the route is what the handler reads (`AuthAction.Of`), authority edits take
  their action from `EditKind`, and `GET /auth/admin/operations` is built from exactly those
  (`AdminOperations`). A handler that checks an action it does not read from its route is an operation the
  manifest does not describe.
- **Every `auth:*` action needs a recent sign-in, and only after the evaluator allows it** (`AdminAccess`),
  so nobody is sent to type a password for something they would be refused anyway.
- **The provider's actions are declared in `AuthActions`,** and `OwnActionsCatalog` enters them into the
  catalog on every start.
- **A store that cannot be read is `503`, never `403`.** "We could not find out" is not "they may do
  nothing", and the second locks out the Owner mid-incident.
- **Every endpoint is a plain `RequestDelegate` and every wire shape has source-generated metadata.** The
  routing overloads that bind an arbitrary delegate reflect over parameters, which Native AOT cannot do,
  and the failure appears at publish time. A shape missing from `ServerJsonContext` throws at runtime.
- **Refusals are `{"error":{"code","message"}}`** (`ErrorEnvelope`).
- **The CORS allowance is a registered client's origin, never a wildcard and never with credentials,** and
  is answered only on what a client reads across origins (the list is `reference/oidc-provider.md`, Across
  origins), never on anything that reads the provider's cookie.
- **Disabling or deleting an account goes through the administration rules** (`AuthorityRules`); the first
  account an empty store gets is granted Owner.
- **`tks-auth owner grant <username>` is the Owner recovery path,** a mode of this binary run as the
  daemon's service account. It reads the daemon's own settings, bypasses the administration rules (whoever
  can run it already holds the file), records `auth.assignment.granted` with `local:<user>` as the actor,
  and refuses a store at a schema version it cannot read.

## Its audit

- **The provider writes its own audit as structured log lines,** `audit <event> actor=… origin=… {payload}`
  (`AuditLog`), so `journalctl -u tks-auth` is the record. Names and payloads come from `Auth.Journal`.
- **A sign-in is recorded where a session is minted, which is one place:** `MintSessionFor`, reached from
  the code exchange and the device grant at `/token`. A second mint site is a second place to forget the
  line, and forgetting is silent. The token exchange mints no session; it records `auth.token.exchanged`
  where it mints.
- **One line per fact that changed, never one per request,** and only when the action actually happened.
  Writing it never fails the action.

## Sessions and credentials

- **Every session lives under a sign-in made here:** a provider session, a session minted under one, or a
  device's session (`kind = 'device'`). A row that is none of these is deleted as the registry opens.
- **Changing what proves an account asks for the credential again; nothing else here does.** The proof is
  the provider session's `credential_at`, stamped whenever a credential is typed on that browser. Detaching
  an identity and setting a password carry the same gate; attaching and detaching ship together or not at
  all.
- **The link callback is a different address from the sign-in one,** and both are registered against the
  provider's application. Freshness is checked when a link starts, never on the way back.
- **Sessions are listed and ended here, because they exist only here,** under every credential handle the
  account holds (a session is keyed by the handle somebody arrived with). Ending one is never gated on
  holding a capability; ending someone else's is addressed under the account it belongs to.

## The catalog and the authority

- **The catalog is kept here, from every source's report.** `AuthorityIntake` is the one place a report
  lands, and the one place what it changed is journaled. A report is held under `application:<id>` for an
  application and under `provider` for tks-auth's own actions.
- **An authority edit is decided by `AuthorityRules`, never by the endpoint.** `AuthorityEndpoints` maps a
  request to an `AuthorityEdit`, pre-checks it for the rule's code, and the store checks it again in the
  applying transaction. `POST /auth/admin/authority/checks` asks the same rules and writes nothing.
- **An assignment is an account and a role,** held organization-wide.

## Applications (`ApplicationRegistry`, `ApplicationCatalog`, `ClientAuthentication`)

- **Every client belongs to an application, and the application decides what is minted.** tks-auth's own
  admin routes are the built-in application (`ApplicationRegistry.Provider`: id `auth`, audience
  `tks-auth`); its one client, `tks-auth`, is declared from the issuer by `ClientRegistry`, never stored,
  and nothing registers over it. Every other application is a row in the session store's `applications`.
- **One code path, two doors.** `ApplicationRegistry` holds every rule and every audit line for
  registering, changing and removing an application, adding and removing a client and rotating a secret.
  `/auth/admin/applications` and `tks-auth app ...` both call it. A rule written in a door is a second
  implementation.
- **The registry is read from memory and reloaded by generation.** Every write to the clients or
  applications advances `registry_meta.generation` in its own transaction, and `ClientRegistry` re-reads
  when a read finds it moved, at most once a second.
- **An application's audience is its own,** never the issuer, `tks-auth` or another application's; its id
  is its action namespace and is never `auth`.
- **An application owns its id and every component its manifest document lists** (`manifests`), read from
  the report held under `application:<id>` (`AuthoritySnapshot.NamespacesOf`). `HeldAsync` and a role that
  holds every action of the application both resolve its actions through that set. A listed component is
  refused when it is `auth`, declared twice or another application's.
- **`MintPairAsync` evaluates `tks_actions` at every mint,** the code exchange and every refresh alike, so
  the lifetime bounds how long a removed role keeps working there.
- **A confidential client proves its secret before any grant is read** (`ClientAuthentication`), so a code
  is never spent by a request that could not have used it. PKCE is required of every code-flow client. A
  secret is shown once and stored as its SHA-256 hash, and is never logged or listed.
- **A session is continued only for the client it was minted for** (`sessions.client_id`);
  `Endpoints.CheckHolder` is the one rule for `/revoke` and the refresh grant.
- **Removing an application or one of its clients ends every session minted through it,** in the
  transaction that removes the rows.
- **An application's manifest is pulled, and refused whole when it speaks for anyone else**
  (`ApplicationCatalog`, `ApplicationManifestWorker`). One that cannot be read changes nothing, so an
  application whose server is briefly down keeps every grant.
- **A public client is registered with `tks-auth app client add <application> --client-id <id> --redirect
  <uri> --post-logout <uri>`;** `--confidential` makes a secret, `--device` a device client.

## The token exchange (`TokenExchange`, `DiscordAuthorizations`)

- **Never add a third subject token type that takes a Discord identity on a client's word** without either a
  token Discord vouches for or the act-for flag.
- **The account decides the answer;** pending, disabled and unknown are `invalid_grant` with
  `account_status`, never a token and never a bare error.
- **A Discord token nobody holds provisions a pending account through
  `IdentityLinkService.ResolveOrProvisionAsync`; a Discord id does not.** An outage at Discord or the store
  decides nothing and writes nothing.
- **What Discord says about a token is cached by the token's SHA-256, never the token,** bounded by entry
  count and capped at `Auth__DiscordTokenCacheMinutes`; a refusal and an outage are never held.

## The device grant (`DeviceAuthorization`, `DevicePageEndpoints`)

- **Both codes are stored hashed,** in `device_authorizations` beside the sessions.
- **A poll is answered from one transaction.** An approval is handed to exactly one poll and its row deleted
  with it.
- **The device's session is minted by `MintSessionFor` under no provider session,** so the approving
  browser's sign-out does not end it.
- **The account decides twice:** approving needs an active account behind the browser's provider session,
  and collecting resolves the approving identity again.
- **Nothing is approved without the person seeing what:** the decision is a separate post behind the
  same-origin gate a credential post passes (RFC 8628 section 5.4). The page's own sign-in is a request in
  flight for the `tks-device` pseudo-client, never registered and never issued a code.
- **Codes that name no device are limited by `DeviceCodeGuesses`,** in memory on purpose.
- **A device's name is cleaned in one place, `DeviceAuthorization.CleanName`,** for what the device sends
  and for what the person types alike, and is cut rather than refused. It lives on the session
  (`sessions.name`), and `tks_device_name` is read from there at every mint, never carried over from the
  previous token. The account page renames it (`POST /account/sessions/name`), and the device learns the
  name at its next refresh.
- **`/revoke` ends a session through `Endpoints.EndSessionAsync`, the path every single-session end takes,**
  is no oracle (RFC 7009 section 2.2), and ends nothing for an access token.

## The Discord round trip (`DiscordDirectory`, `OAuthHandshake`)

- **It answers who, and only who.** `DiscordDirectory` is an `IIdentityProvider` and the only chokepoint to
  `discord.com`. It takes one `OAuthApplication`, holds no guild, reads no role and takes no bot token.
  `DiscordAuthException` derives from `AuthProviderException`.
- **The caller's token buys one thing and is dropped:** it is presented to `users/@me` or `oauth2/@me` and
  never stored.
- **Register it transient, and resolve it once per composition.** It is a typed `HttpClient`; a singleton
  pins a handler and stops DNS changes landing. The composition hands the same instance to both halves.
- **A bad code is `null`; an outage throws.** A 4xx from the token endpoint is the caller's problem (`401`),
  a 5xx or an unreachable host is `DiscordAuthException` (`502`).
- **`OAuthHandshake` is its own type, not Discord's,** and every provider's round trip uses it unchanged.
- **`state` and PKCE ride one cookie and neither is optional;** the cookie is `SameSite=Lax`, never
  `Strict`, which suppresses it on the redirect back and breaks every login.

## The OpenID Connect provider

- **The issuer is the provider's one address, set by the deployment and never inferred from a request.**
  Browsers and every provider callback use its origin; a second name for the same daemon would start a
  round trip whose cookie the callback never sees. A provider whose issuer is not an absolute URL answers
  `no_issuer` at every OIDC door.
- **Until the client and its redirect are known to be registered, a refusal is rendered on the provider's
  own origin;** redirects are matched exactly.
- **The request in flight lives here, behind `tks_auth_authz`;** the page, its form and its provider links
  carry no request field.
- **A credential post is refused before the credential is read** unless it passes the same-origin gate.
  Moving the check after the password check turns it into a lockout anybody can trigger.
- **A code is taken out of the store before anything about the exchange is checked,** by one
  delete-returning statement, once the client has authenticated.
- **Every session minted through the provider records its provider session, in one column set at mint.** A
  mint site that skips it strands a surface signed in after its browser signed out.
- **The account is re-read on every pass,** cookie or no cookie.
- **An external provider's round trip completes the request in flight only when its returning `state` is
  the one the request recorded.**
- **A surface's client id is its origin's host** (a site derives it from where it was loaded).
- **`id_token`'s subject is the account, its audience the client;** it is never accepted as a bearer.
- **The pages are this repo's `web/`, served as built from `UiPath`.** The provider writes the provider
  links at the marker and nothing else, reads the files per request, and serves `/ui/` only from under
  `UiPath` (a resolved path outside it is a 404: this daemon can open its signing key). With no pages
  installed the built-in floor still signs people in.
- **The account page is the only place a credential changes,** and its sign-in is a request in flight for
  the `tks-account` pseudo-client. A provider round trip begun to re-prove the person accepts only an
  identity already attached to that account and never mints anything.
