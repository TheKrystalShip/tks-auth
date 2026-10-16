# @thekrystalship/auth-client

The browser sign-in client for tks-auth. A site configures it with an issuer and a client id and gets a
session that keeps itself signed in: it renews ahead of expiry, renews and replays a refused call,
holds through a network outage or an unreachable anchor, and settles races between tabs. The only state
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
| `restore()` | On the callback path, completes the sign-in and returns the path the sign-in carried (null for anything else). Elsewhere loads the stored session and starts the renewal timer. Never throws for a network failure. |
| `signIn({ returnTo })` | Full-page redirect to the anchor. `returnTo` defaults to the current path, query and hash and rides in the OIDC `state`; only a path on the site's own origin is handed back. Resolves `false` when the anchor could not be asked. |
| `fresh()` | A token valid for at least `renewAheadS`, renewing if needed. Rejects (`error.code` is `ended` or `none`) only when there is no session. While the session is `stale` it resolves with the held token. |
| `authorizedFetch(input, init)` | `fetch` with the bearer. A 401 renews once and replays once. `init.fresh = true` renews first. A body that is a stream cannot be replayed. |
| `signOut()` | A browser session redirects to the anchor's end-session with `id_token_hint`. A device session revokes its refresh token. Either way the status is `none`. |
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
| `none` | Nobody is signed in. | Its signed-out view, or leaves for the anchor. |
| `restoring` | `restore()` is running. | Its splash, or nothing. |
| `live` | Signed in. | The signed-in app. |
| `stale` | Offline, the anchor is unreachable, a renewal is being retried, or a refusal is inside its grace. | The signed-in app, unchanged, with nothing about the session. |
| `ended` | The session is over and nothing the client can do will bring it back. `reason` is `refused` (the anchor refused the refresh token), `revoked` (the session was ended: `end()` was called, or another tab signed out) or `denied` (the account may not use this client). | One screen with one action: sign in again. |

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
  redirectPath: "/signed-in",      // the callback; a real path, registered at the anchor
  postSignOutPath: "/",            // registered at the anchor
  graceMs: 30000,
  renewAheadS: 60,
});
```

`resolve` is a site's own discovery. It answers from its own cache when the network is down, because
the stored session cannot be found without the client id. If it throws, `restore()` leaves the status
`none`.

The client requests `openid` with authorization code and PKCE S256, and renews through the refresh
grant alone: no iframe, no `prompt=none`.

## Development

```bash
npm ci
npm test        # Vitest over a fake anchor and fake timers
npm run check   # tsc over the JSDoc types
npm run build   # dist/*.js and the emitted .d.ts
```
