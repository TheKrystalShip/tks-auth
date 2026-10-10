# `Auth.Anchor` (`tks-auth`) — locked decisions

The OpenID Connect provider that holds the accounts and signs people in, on its own or as the member of a
KGSM cluster that signs people in to the whole cluster at once. Built from this repo's libraries by project
reference. Canon: `kgsm-docs/systems/identity/` (`sign-in.md`, `sessions.md`, `applications.md`,
`cluster.md`), `kgsm-docs/reference/oidc-provider.md`, `session-tokens.md` and `auth-cluster-wire.md`;
this repo's `README.md` documents the doors. Minting is `Minting/CLAUDE.md`.

## The core and the KGSM module

- **`Program.cs` composes the core, and the KGSM module only when a cluster is configured.**
  `TksAuthCore` (`AddTksAuthCore`, `MapTksAuthCore`) is accounts, sessions, the OpenID Connect doors, the
  applications and their clients, the account page, the admin and authority routes and the journal.
  `KgsmModule` (`AddKgsmModule`, `MapKgsmModule`) is membership and the bus, the DNS member,
  `ClusterMembershipWorker`, the catalog's handlers and `AuthorityReporter`, `MemberDepartureWorker`,
  `AuthorityBroadcast` and its worker, `SessionBroadcast`, the member snapshot, the roster,
  `/auth/cluster/public-key` and the component surface.
- **One switch: the cluster secret.** `KgsmModule.ClusterFrom` reads it; `ClusterOptions.Enabled` decides
  whether the module is composed and is what `AnchorRole` is built from, so the standing and the
  composition cannot disagree. There is no second setting.
- **Every KGSM path is the module's.** `/etc/kgsm/cluster-founded`, `/var/lib/kgsm/anchors` and the cluster
  store are reached only from module code. A standalone install reads and writes nothing under `/etc/kgsm`
  or `/var/lib/kgsm`; `StandaloneAnchorTests` asserts that and that no module service is registered.
- **The core tells other members things only through `ISessionAnnouncer` and `IAuthorityAnnouncer`.** The
  module binds them to `SessionBroadcast` and `AuthorityBroadcast`; a standalone anchor binds both to
  `StandaloneAnnouncements`. A core class that resolves a module service directly breaks every standalone
  start.
- **A standalone anchor's own actions enter its catalog from the binary, never from disk.**
  `StandaloneCatalog` reports `AuthActions.Declared` on every start. In a cluster the reporter reads the
  installed manifest instead, and the two never both run: a report replaces the member's last one
  wholesale. The standalone report carries no `auth:config.*` or `auth:journal.read`, since the surface
  those gate is the module's.
- **Three standings, and collapsing the first into the third breaks every standalone install.** A machine
  with no cluster secret is not "not the holder": its accounts are its own and it serves everything.
  `AnchorRole` is where that lives, and a clustered anchor starts at `StandingBy` rather than assuming it
  holds the capability.
- **`TryClaimAsync` returning true is not holding it.** It is compare-and-set against what this member
  knows, so two isolated anchors both succeed. Every claim is followed by a re-read, and a member that
  finds itself not the holder stands down.
- **Only the anchor on the machine that founded the cluster claims the accounts into an empty
  assignment** (`ClusterFounding.IsFoundedHere`). Anywhere else an empty assignment means gossip has not
  arrived.
- **The package is preset-disabled.** Which machine holds the anchor is decided under
  `api:members.manage`; a second installed machine is a promotion candidate, not a second authority.

## The daemon

- **A KGSM session's audience is the cluster, not a machine.** It reaches the minter as
  `SessionTokenOptions.Audience`; changing `Anchor__ClusterId` on a running cluster invalidates every token.
  Every other application's tokens carry that application's audience.
- **Signing is asymmetric, `ValidAlgorithms` is pinned, and the private key is generated exactly once.** A
  file that exists and cannot be read stops the daemon and is never a reason to generate. It is created
  with mode `0600`, not chmod'd after.
- **The anchor runs on the account store at schema version 2,** `SqliteAuthorityStore`, which is both the
  authority and the `IUserStore` every door reads. The file is `/var/lib/tks-auth/accounts.db`, never the
  node's replica on the same machine (`/var/lib/kgsm/auth/users.db`): a node applying the snapshot to the
  file the anchor writes would be the authority rewritten by its own echo.
- **A KGSM session proves who, never what.** Every route decides its caller by an action,
  `Endpoints.RequireCaller(ctx, action)`, evaluated against the authority and, for `auth:*`, held to a
  recent sign-in. Two kinds of bearer are accepted at this daemon's own doors (`SessionReader`): a KGSM
  session and a session of tks-auth's own application audienced to `tks-auth`; any other application's token
  is that application's alone.
- **A gated route declares its action on itself, and that declaration is both enforced and published.**
  `AuthAction` metadata on the route is what the handler reads (`AuthAction.Of`), the own-surface group is
  marked for `OwnSurfaceFilter`, authority edits take their action from `EditKind`, and
  `GET /auth/cluster/operations` is built from exactly those (`AnchorOperations`). A handler that checks an
  action it does not read from its route is an operation the manifest does not describe.
- **Every `auth:*` action needs a recent sign-in, and only after the evaluator allows it** (`AnchorAccess`),
  so nobody is sent to type a password for something they would be refused anyway.
- **The anchor's actions are declared twice and held together by a test.** `[Action]` on this assembly writes
  the manifest; `AuthActions` in `Auth.Access` names them for every evaluator. `AnchorManifestTests` fails
  when the two disagree.
- **A store that cannot be read is `503`, never `403`.** "We could not find out" is not "they may do
  nothing", and the second locks out the Owner mid-incident.
- **Every endpoint is a plain `RequestDelegate` and every wire shape has source-generated metadata.** The
  routing overloads that bind an arbitrary delegate reflect over parameters, which Native AOT cannot do, and
  the failure appears at publish time. A shape missing from `AnchorJsonContext` throws at runtime.
- **The CORS allowance is a registered client's origin, never a wildcard and never with credentials,** and is
  answered only on what a client reads across origins (the list is `reference/oidc-provider.md`, Across
  origins), never on anything that reads the provider's cookie.
- **Disabling or deleting an account goes through the administration rules** (`AuthorityRules`); the first
  account an empty store gets is granted Owner.
- **`tks-auth owner grant <username>` is the Owner recovery path,** a mode of this binary run as the
  anchor's service account. It reads the daemon's own settings, bypasses the administration rules (whoever
  can run it already holds the file), journals `auth.assignment.granted` with `local:<user>` as the actor,
  and refuses a store at any other schema version.

## Its journal

- **The anchor writes its own event journal, and it is the only witness there is.** The producer id has to
  be the name in the unit's `StateDirectory=`, because a reader establishes the producer from the path it
  read a line out of; a journal written elsewhere is simply never found. A test run relocates it with
  `KGSM_JOURNAL_STATE_ROOT`; left at its default, a suite that signs people in appends invented sign-ins to
  a live audit page.
- **A sign-in is recorded where a session is minted, which is one place:** `MintSessionFor`, reached from
  the code exchange and the device grant at `/token`. A second mint site is a second place to forget the
  line, and forgetting is silent. The token exchange mints no session; it records `auth.token.exchanged`
  where it mints.
- **One line per fact that changed, never one per request,** and only when the action actually happened.

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
- **The session registry is the anchor's own, on its own file.** A member replicating the accounts
  replicates none of the sign-ins.

## Administering itself

- **In a KGSM cluster the anchor administers itself.** The module serves `/auth/config` and `/auth/logs`
  from the daemon being configured and read, on `auth:config.*` and `auth:journal.read`
  (`kgsm-docs/plans/component-surfaces.md`).
- **The unit both surfaces name is the descriptor's, never a second setting,** and carries
  `SupplementaryGroups=systemd-journal`, without which `journalctl` exits 0 having printed nothing.
- **One `journalctl -f` for however many people are watching.** The first subscriber starts the follow, the
  last stops it, and a subscriber that falls behind drops its own oldest lines. The stream carries no
  backlog and opens with a comment line, then heartbeats.
- **A test relocates every absolute path this daemon reads, not only the ones it writes:**
  `ConfigDescriptorPath` and `ConfigOverridePath` join the stores and the journal root, or a run measures the
  host.
- **The catalog is kept here, from every member's report.** `AuthorityIntake` is the one place a report or
  an uninstall lands, and the one place what it changed is journaled, attributed as `system:<member>`. An
  uninstall is matched on the sender's member id and the nonce, never on a node the message names.
- **A member's report is forgotten only when the roster marks it left** (`MemberDepartureWorker`); a crashed
  member is marked dead, and treating that as removal would strip a node down for maintenance of every
  grant it carries.
- **Every write reaches the cluster from the store's outbox, never from the write path.**
  `AuthorityBroadcast` drains `authority_outbox` after each write and on a timer and clears a row only after
  the bus holds it. The heartbeat goes only to members the roster shows alive, and the staleness bound is
  held to at least two heartbeats.
- **An authority edit is decided by `AuthorityRules`, never by the endpoint.** `AuthorityEndpoints` maps a
  request to an `AuthorityEdit`, pre-checks it for the rule's code, and the store checks it again in the
  applying transaction. `POST /auth/cluster/authority/checks` asks the same rules and writes nothing.

## Applications (`ApplicationRegistry`, `ApplicationCatalog`, `ClientAuthentication`)

- **Every client belongs to an application, and the application decides what is minted.** KGSM is the
  built-in application (`ApplicationRegistry.Kgsm`), never stored and unchangeable here. tks-auth's own
  admin routes are the other built-in (`ApplicationRegistry.Provider`: id `auth`, audience `tks-auth`); its
  one client, `tks-auth`, is declared from the issuer by `ClientRegistry`, never stored, and nothing
  registers over it. Every other application is a row in the session store's `applications`.
- **One code path, two doors.** `ApplicationRegistry` holds every rule and every journal line for
  registering, changing and removing an application, adding and removing a client and rotating a secret.
  `/auth/cluster/applications` and `tks-auth app ...` both call it, the command composing it over the
  daemon's own files with `DeferredAnnouncements`. A rule written in a door is a second implementation.
- **The registry is read from memory and reloaded by generation.** Every write to the clients or
  applications advances `registry_meta.generation` in its own transaction, and `ClientRegistry` re-reads when
  a read finds it moved, at most once a second.
- **An application's audience is its own,** never the cluster id, the issuer, `tks-auth` or another
  application's; its id is its action namespace and is never `kgsm`, `auth` or a namespace a KGSM member
  declares.
- **`MintPairAsync` evaluates `tks_actions` at every mint,** the code exchange and every refresh alike, so the
  lifetime bounds how long a removed role keeps working there.
- **A confidential client proves its secret before any grant is read** (`ClientAuthentication`), so a code is
  never spent by a request that could not have used it. PKCE is required of every code-flow client. A
  secret is shown once and stored as its SHA-256 hash, and is never journaled, logged or listed.
- **A session is continued only for the client it was minted for** (`sessions.client_id`);
  `Endpoints.CheckHolder` is the one rule for `/revoke` and the refresh grant.
- **Removing an application or one of its clients ends every session minted through it,** in the transaction
  that removes the rows.
- **An application's manifest is pulled, and refused whole when it speaks for anyone else**
  (`ApplicationCatalog`, `ApplicationManifestWorker`, only while this anchor holds the accounts). One that
  cannot be read changes nothing, so an application whose server is briefly down keeps every grant.

## The token exchange (`TokenExchange`, `DiscordAuthorizations`)

- **Never add a third subject token type that takes a Discord identity on a client's word** without either a
  token Discord vouches for or the act-for flag.
- **The account decides the answer;** pending, disabled and unknown are `invalid_grant` with
  `account_status`, never a token and never a bare error.
- **A Discord token nobody holds provisions a pending account through
  `IdentityLinkService.ResolveOrProvisionAsync`; a Discord id does not.** An outage at Discord or the store
  decides nothing and writes nothing.
- **What Discord says about a token is cached by the token's SHA-256, never the token,** bounded by entry
  count and capped at `Anchor__DiscordTokenCacheMinutes`; a refusal and an outage are never held.

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
  flight for the `kgsm-device` pseudo-client, never registered and never issued a code.
- **Codes that name no device are limited by `DeviceCodeGuesses`,** in memory on purpose.
- **`/revoke` ends a session through `Endpoints.EndSessionAsync`, the path every single-session end takes,**
  is no oracle (RFC 7009 section 2.2), and ends nothing for an access token.

## The Discord round trip (`DiscordDirectory`, `OAuthHandshake`)

- **It answers who, and only who.** `DiscordDirectory` is an `IIdentityProvider` and the only chokepoint to
  `discord.com`. It takes one `KgsmOAuthApplication`, holds no guild, reads no role and takes no bot token.
  `DiscordAuthException` derives from `KgsmAuthProviderException`.
- **The caller's token buys one thing and is dropped:** it is presented to `users/@me` or `oauth2/@me` and
  never stored.
- **Register it transient, and resolve it once per composition.** It is a typed `HttpClient`; a singleton
  pins a handler and stops DNS changes landing. The composition hands the same instance to both halves.
- **A bad code is `null`; an outage throws.** A 4xx from the token endpoint is the caller's problem (`401`),
  a 5xx or an unreachable host is `DiscordAuthException` (`502`).
- **`OAuthHandshake` is its own type, not Discord's,** and every provider's round trip uses it unchanged.
- **`state` and PKCE ride one cookie and neither is optional;** the cookie is `SameSite=Lax`, never `Strict`,
  which suppresses it on the redirect back and breaks every login.

## The OpenID Connect provider

- **The issuer is configuration, never inferred from a request,** and an anchor whose issuer is not an
  absolute URL answers `no_issuer` at every OIDC door.
- **Until the client and its redirect are known to be registered, a refusal is rendered on the anchor's own
  origin;** redirects are matched exactly.
- **The request in flight lives here, behind `kgsm_authz`;** the page, its form and its provider links carry
  no request field.
- **A credential post is refused before the credential is read** unless it passes the same-origin gate.
  Moving the check after the password check turns it into a lockout anybody can trigger.
- **A code is taken out of the store before anything about the exchange is checked,** by one
  delete-returning statement, once the client has authenticated.
- **Every session minted through the provider records its provider session, in one column set at mint.** A
  mint site that skips it strands a surface signed in after its browser signed out.
- **The account is re-read on every pass,** cookie or no cookie.
- **An external provider's round trip completes the request in flight only when its returning `state` is the
  one the request recorded.**
- **A member's client is paths joined to its roster address, never URLs it names** (`ClusterClientAnnouncement`),
  and a surface's client id is its origin's host (`ClientIdFor`).
- **`auth.origins` is published while this anchor holds the accounts,** and an application outside KGSM's
  origins are admitted here alone.
- **A panel on a static host is declared, not stored.** `Anchor__PanelOrigins` is rebuilt on every start and
  wins over a stored client of the same id.
- **`id_token`'s subject is the account, its audience the client;** it is never accepted as a bearer.
- **The pages are this repo's `web/`, served as built from `UiPath`.** The anchor writes the provider links
  at the marker and nothing else, reads the files per request, and serves `/ui/` only from under `UiPath`
  (a resolved path outside it is a 404: this daemon can open its signing key). With no pages installed the
  built-in floor still signs people in.
- **The account page is the only place a credential changes,** and its sign-in is a request in flight for
  the `kgsm-account` pseudo-client. A provider round trip begun to re-prove the person accepts only an
  identity already attached to that account and never mints anything.
