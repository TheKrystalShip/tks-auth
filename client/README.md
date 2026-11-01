# @thekrystalship/auth-client

The browser sign-in client for tks-auth. A site configures it with an issuer and a client id and gets a
session that keeps itself signed in: it renews ahead of expiry, renews and replays a refused call,
holds through a network outage or an unreachable provider, and settles races between tabs. The only state
a site is meant to draw is the end of a session nothing can recover.

How signing in works is canon in `kgsm-docs/systems/identity/sign-in.md` and `sessions.md`. The package
wraps `oidc-client-ts` completely; a site imports nothing from it. It depends on no design system and
no framework: the core is plain ES modules, and `@thekrystalship/auth-client/react` adds one hook.

## Use

```js
import { createSession } from "@thekrystalship/auth-client";
import { useSession } from "@thekrystalship/auth-client/react";

export const session = createSession({ issuer: "https://auth.example.com", clientId: "example-site" });

const returnTo = await session.restore();   // on boot
const snapshot = useSession(session);        // in a component
const response = await session.authorizedFetch("/api/things");
```

| Call | Does |
|---|---|
| `restore()` | On the callback path, completes the sign-in and returns the path the sign-in carried (null for anything else); a callback that fails leaves the status `none`, so the site signs in again, and a second failure within a minute is `ended` (`refused`), so it never loops. Elsewhere loads the stored session and starts the renewal timer. Never throws for a network failure. |
| `signIn({ returnTo })` | Full-page redirect to the provider. `returnTo` defaults to the current path, query and hash and rides in the OIDC `state`; only a path on the site's own origin is handed back. Resolves `false` when the provider could not be asked. |
| `fresh({ force })` | A token valid for at least `renewAheadS`, renewing if needed; `force: true` renews whatever the expiry says, for a request a server refused that `authorizedFetch` cannot replay (a stream, a worker's upload). Rejects (`error.code` is `ended` or `none`) only when there is no session. While the session is `stale` it resolves with the held token. |
| `token()` | The token held now, or null without a session, for a request that cannot wait on a promise (a media element's source). |
| `authorizedFetch(input, init)` | `fetch` with the bearer. A 401 renews once and replays once. `init.fresh = true` renews first. A body that is a stream cannot be replayed. |
| `signOut()` | A browser session redirects to the provider's end-session with `id_token_hint`. A device session revokes its refresh token. Either way the status is `none`. |
| `adopt(tokens, renew)` | Holds a session minted elsewhere. `tokens` is `{ access_token | token, expires_in | expiresIn | expires_at }`; `renew()` resolves the next tokens, rejects when the minter cannot be reached, and resolves nothing when there is no more. Nothing is stored. |
| `end(reason)` | The site's own certain end (`refused`, `revoked` or `denied`). |
| `updateAccount(patch)` | Merges into `snapshot.account`; the patch survives renewals. |
| `subscribe(listener)`, `getSnapshot()` | The store contract. The snapshot is the same object until something changes. |
| `device.start({ name })` | Starts a device authorization. Resolves `{ userCode, verificationUri, verificationUriComplete, expiresIn, poll, cancel }`. `poll()` resolves `signed-in`, `refused`, `expired` or `cancelled`; it honours `slow_down` and keeps polling through network failures. A signed-in device is stored like any session and renews like one. |

## States

The snapshot is `{ status, reason, account, claims, expiresAt }`. `account` is `{ id, name, username }`
read from the access token; `claims` is the token's decoded payload; `expiresAt` is milliseconds.

| Status | Means | A site draws |
|---|---|---|
| `none` | Nobody is signed in. | Its signed-out view, or leaves for the provider. |
| `restoring` | `restore()` is running. | Its splash, or nothing. |
| `live` | Signed in. | The signed-in app. |
| `stale` | Offline, the provider is unreachable, a renewal is being retried, or a refusal is inside its grace. | The signed-in app, unchanged, with nothing about the session. |
| `ended` | The session is over and nothing the client can do will bring it back. `reason` is `refused` (the provider refused the refresh token), `revoked` (the session was ended: `end()` was called, or another tab signed out) or `denied` (the account may not use this client). | One screen with one action: sign in again. |

`ended` is reached only after a refusal that survives a recheck of storage and, for a session that was
live in the page, `graceMs` without recovery. A refusal of the stored session at boot, once it has
survived the lock and the recheck, ends at once: there is no last good view to hold. A network failure is never a refusal: it makes the session `stale` and retries after 15 s,
doubling to a 120 s cap, and the `online` event and the page becoming visible retry at once.

## Behaviour that does not need a site

- **One renewal in flight per client across tabs.** A Web Lock named from the client id serializes
  renewal; after the lock the stored session is read first, and another tab's newer token is used
  instead of spending the refresh token. Where Web Locks are missing, renewals are single-flight in the
  tab.
- **Another tab's changes are followed.** A newer stored session is taken up; a removed one ends this
  tab as `revoked`.
- **Storage.** The user is in `localStorage` and a sign-in in flight in `sessionStorage`, under keys
  prefixed `tks-auth-client:<clientId>:`. Two clients on one origin do not collide.

## Configuration

```js
createSession({
  issuer, clientId,                // or resolve: async () => ({ issuer, clientId })
  redirectPath: "/signed-in",      // the callback; a real path, registered at the provider
  postSignOutPath: "/",            // registered at the provider
  graceMs: 30000,
  renewAheadS: 60,
});
```

`resolve` is a site's own discovery. It answers from its own cache when the network is down, because
the stored session cannot be found without the client id. If it throws, `restore()` leaves the status
`none`.

The client requests `openid` with authorization code and PKCE S256, and renews through the refresh
grant alone: no iframe, no `prompt=none`.

## The site's API

`@thekrystalship/auth-client/api` is a site's calls to its own API over the session: bodies go in and come
out as JSON, the bearer rides on `authorizedFetch` (renewed and replayed once on a 401), and every failure
is an `ApiError` with a sentence fit to show.

```js
import { createApi, ApiError } from "@thekrystalship/auth-client/api";

export const api = createApi({ session, base: () => config.api, headers: () => ({ "Accept-Language": lang }) });

await api.get("/recipes");             // also post, put, patch, delete; upload(path, formData)
await api.stream("/imports", body, (line) => ...);   // an application/x-ndjson answer, line by line
const photo = await api.blob("/photos/1");
```

| Option | Does |
|---|---|
| `base` | Prefixed to every path. A function is asked on each call, and an empty answer fails the call as "no API is configured" without sending it. |
| `headers` | Added to every request, asked on each call. |
| `credentials` | The requests' `credentials`, such as `omit` on an origin whose own cookie must not ride along. |
| `anonymous` | With nobody signed in, the call goes out without a bearer instead of failing. |
| `endOnRefusal` | A 401 the renewal did not cure ends a live session as `refused`. |
| `answered`, `onReach` | Whether a response came from the site's own server and not a gateway or a portal (one that did not is a status-0 failure), and a hook told after each call whether the server answered. |
| `words` | The sentences a failure with no body of its own says: `unreachable`, `unconfigured`, `signIn`, and `failed(status, statusText)`, which by default is the status text. |

`ApiError` carries `status` (0 when no server answered, 401 when there is no session), the answer's
sentence as its message, its `code` and the parsed `body`. The sentence and code are read from whichever of
the organization's error shapes the answer is: `{ error: { code, message } }`, `{ error, detail }`,
`{ error }`, or a problem's `{ title, detail }` (`failureOf`).

## Tests

`@thekrystalship/auth-client/testing` holds `createStandInSession()`: the session's interface with no
provider behind it. A test signs a person in with `adopt({ claims })`, ends the session with `end(reason)`
and reads it through `useSession` like any other.

## Development

```bash
npm ci
npm test        # Vitest over a fake provider and fake timers
npm run check   # tsc over the JSDoc types
npm run build   # dist/*.js and the emitted .d.ts
```
