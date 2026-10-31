import { ErrorResponse, UserManager, WebStorageStateStore } from "oidc-client-ts";
import { accountOf, claimsOf } from "./claims.js";
import { startDevice } from "./device.js";

/** @typedef {import("./types.js").Session} Session */
/** @typedef {import("./types.js").Snapshot} Snapshot */
/** @typedef {import("./types.js").SessionConfig} SessionConfig */
/** @typedef {import("./types.js").AdoptedTokens} AdoptedTokens */
/** @typedef {import("./types.js").Account} Account */
/** @typedef {import("./types.js").EndReason} EndReason */
/** @typedef {import("./types.js").Status} Status */
/** @typedef {import("./types.js").DeviceCode} DeviceCode */

export { DeviceSignInError } from "./device.js";
export { claimsOf } from "./claims.js";

/** Every request to the provider is bounded: a token endpoint that never answers holds every renewal. */
const PROVIDER_TIMEOUT_S = 10;
const RETRY_FIRST_MS = 15000;
/** How soon a second failed sign-in callback counts as a loop. */
const CALLBACK_RETRY_WINDOW_MS = 60000;
const readSession = (key) => { try { return globalThis.sessionStorage.getItem(key); } catch { return null; } };
const writeSession = (key, value) => { try { globalThis.sessionStorage.setItem(key, value); } catch { /* storage refused */ } };
const removeSession = (key) => { try { globalThis.sessionStorage.removeItem(key); } catch { /* storage refused */ } };
const RETRY_CAP_MS = 120000;
/** The floor under the renewal timer, so a token shorter-lived than `renewAheadS` does not spin. */
const TIMER_FLOOR_MS = 5000;

/** OAuth errors from the token endpoint that mean the refresh token is over. Anything else is not a verdict. */
const REFUSALS = new Set(["invalid_grant", "invalid_token", "invalid_client", "unauthorized_client"]);
const REASONS = new Set(["refused", "revoked", "denied"]);

/** @type {Record<string, any>} */
const NO_CLAIMS = Object.freeze({});
const INITIAL = Object.freeze({ status: "none", reason: null, account: null, claims: NO_CLAIMS, expiresAt: null });

/** The renewal's verdict on one attempt. `gone` is a stored session another tab removed. */
/** @typedef {"ok" | "unreachable" | "refused" | "denied" | "gone"} Outcome */

class SessionError extends Error {
  /** @param {string} code */
  constructor(code) {
    super(code === "ended" ? "The session has ended." : "There is no session.");
    this.code = code;
  }
}

/** A return-to is kept only when it is a path on this origin. */
function safeReturnTo(value) {
  return typeof value === "string" && value.startsWith("/") && !value.startsWith("//") ? value : null;
}

function sameAccount(a, b) {
  if (a === b) return true;
  return !!a && !!b && a.id === b.id && a.name === b.name && a.username === b.username
    && Object.keys(a).length === Object.keys(b).length;
}

/**
 * A browser's sign-in with the tks-auth provider, as one session object: who is signed in, the token
 * that proves it, and the machinery that keeps it alive without being seen.
 * @param {SessionConfig} [config]
 * @returns {Session}
 */
export function createSession(config = {}) {
  const redirectPath = config.redirectPath ?? "/signed-in";
  const postSignOutPath = config.postSignOutPath ?? "/";
  const graceMs = config.graceMs ?? 30000;
  const renewAheadMs = (config.renewAheadS ?? 60) * 1000;

  /** @type {Snapshot} */
  let snapshot = INITIAL;
  const listeners = new Set();

  // What the snapshot is made of.
  let token = null;
  /** @type {Record<string, any>} */
  let claims = NO_CLAIMS;
  let profile;
  let expiresAt = null;
  let accountPatch = {};
  /** @type {{ renew: () => Promise<AdoptedTokens | null | undefined> } | null} */
  let adopted = null;

  // The provider connection, built on first use.
  /** @type {Promise<{ manager: UserManager, clientId: string, prefix: string }> | null} */
  let readyPromise = null;
  /** @type {UserManager | null} */
  let manager = null;
  let prefix = "";
  let listening = false;

  // Recovery bookkeeping.
  let renewing = null;
  let restoring = null;
  let timer = null;
  let retryTimer = null;
  let retries = 0;
  let endTimer = null;
  /** @type {{ reason: "refused" | "denied", final: boolean } | null} */
  let refusal = null;
  /** True while `restore()` renews a stored session: a refusal then has no last good view to wait over. */
  let booting = false;

  // ---------------------------------------------------------------- snapshot

  function publish(status, reason = null) {
    const account = token ? buildAccount() : null;
    const next = { status, reason, account, claims: token ? claims : NO_CLAIMS, expiresAt: token ? expiresAt : null };
    if (
      next.status === snapshot.status && next.reason === snapshot.reason && next.expiresAt === snapshot.expiresAt
      && next.claims === snapshot.claims && sameAccount(next.account, snapshot.account)
    ) return;
    snapshot = next;
    for (const listener of [...listeners]) listener();
  }

  function buildAccount() {
    const base = accountOf(claims, profile);
    return base ? { ...base, ...accountPatch } : null;
  }

  function setTokens(accessToken, expiryMs, userProfile) {
    if (accessToken !== token) {
      const decoded = claimsOf(accessToken) || {};
      if (claims.sub && decoded.sub !== claims.sub) accountPatch = {};
      token = accessToken;
      claims = decoded;
    }
    profile = userProfile;
    expiresAt = expiryMs;
  }

  function clearTokens() {
    token = null;
    claims = NO_CLAIMS;
    profile = undefined;
    expiresAt = null;
    accountPatch = {};
  }

  /** The stored user's expiry, in milliseconds. */
  function expiryOfUser(user) {
    const exp = user.expires_at ?? claimsOf(user.access_token)?.exp;
    return typeof exp === "number" ? exp * 1000 : null;
  }

  function usable(user) {
    const exp = expiryOfUser(user);
    return !!user.access_token && exp != null && exp - Date.now() > renewAheadMs;
  }

  /** A live session from a stored user: the renewal succeeded, or another tab's did. */
  function holdUser(user) {
    setTokens(user.access_token, expiryOfUser(user), user.profile);
    recovered();
    publish("live");
    schedule();
  }

  /** A live session from tokens minted elsewhere. */
  function holdAdopted(tokens) {
    const access = tokens.access_token ?? tokens.token;
    const lifetime = tokens.expires_in ?? tokens.expiresIn;
    const exp = typeof lifetime === "number" ? Date.now() + lifetime * 1000
      : typeof tokens.expires_at === "number" ? tokens.expires_at * 1000
      : (claimsOf(access)?.exp ?? 0) * 1000;
    setTokens(access, exp || null, undefined);
    recovered();
    publish("live");
    schedule();
  }

  function recovered() {
    clearTimeout(retryTimer);
    clearTimeout(endTimer);
    retryTimer = null;
    endTimer = null;
    retries = 0;
    refusal = null;
  }

  // ---------------------------------------------------------------- timers

  function schedule() {
    clearTimeout(timer);
    timer = null;
    if (snapshot.status !== "live" || expiresAt == null) return;
    const wait = Math.max(TIMER_FLOOR_MS, expiresAt - Date.now() - renewAheadMs);
    timer = setTimeout(() => { timer = null; void renew(); }, wait);
  }

  function scheduleRetry() {
    if (retryTimer) return;
    const wait = Math.min(RETRY_FIRST_MS * 2 ** retries, RETRY_CAP_MS);
    retries += 1;
    retryTimer = setTimeout(() => { retryTimer = null; void renew(); }, wait);
  }

  function endNow(reason) {
    clearTimeout(timer);
    timer = null;
    recovered();
    const stored = manager;
    const wasAdopted = adopted;
    adopted = null;
    clearTokens();
    if (stored && !wasAdopted) void stored.removeUser().catch(() => {});
    publish("ended", reason);
  }

  // ---------------------------------------------------------------- the provider

  function init() {
    readyPromise ??= (async () => {
      const resolved = config.resolve ? await config.resolve() : config;
      const { issuer, clientId } = resolved;
      if (!issuer || !clientId) throw new Error("createSession needs an issuer and a clientId.");
      const origin = globalThis.location.origin;
      prefix = `tks-auth-client:${clientId}:`;
      manager = new UserManager({
        authority: issuer,
        client_id: clientId,
        redirect_uri: origin + redirectPath,
        post_logout_redirect_uri: origin + postSignOutPath,
        response_type: "code",
        scope: "openid",
        userStore: new WebStorageStateStore({ prefix, store: globalThis.localStorage }),
        stateStore: new WebStorageStateStore({ prefix, store: globalThis.sessionStorage }),
        automaticSilentRenew: false,
        monitorSession: false,
        loadUserInfo: false,
        requestTimeoutInSeconds: PROVIDER_TIMEOUT_S,
      });
      attachListeners();
      return { manager, clientId, prefix };
    })().catch((error) => {
      readyPromise = null;
      throw error;
    });
    return readyPromise;
  }

  /** One renewal in flight per client across tabs: the Web Lock where the browser has one. */
  function withLock(fn) {
    const locks = globalThis.navigator?.locks;
    if (!locks || typeof locks.request !== "function") return fn();
    return locks.request(`${prefix}renew`, fn);
  }

  const offline = () => globalThis.navigator?.onLine === false;

  /** @param {unknown} error @returns {"refused" | "denied" | "unreachable"} */
  function classify(error) {
    if (error instanceof ErrorResponse) {
      if (error.error === "access_denied") return "denied";
      if (REFUSALS.has(error.error)) return "refused";
    }
    return "unreachable";
  }

  /** @returns {Promise<Outcome>} */
  async function attempt() {
    if (adopted) return attemptAdopted();
    try {
      await init();
    } catch {
      return "unreachable";
    }
    const replacing = token;
    return withLock(async () => {
      const before = await manager.getUser().catch(() => null);
      if (!before) return "gone";
      // Another tab may have renewed while this one waited for the lock.
      if (before.access_token !== replacing && usable(before)) {
        holdUser(before);
        return "ok";
      }
      if (!before.refresh_token) return "refused";
      if (offline()) return "unreachable";
      try {
        const user = await manager.signinSilent({ silentRequestTimeoutInSeconds: PROVIDER_TIMEOUT_S });
        if (user && user.access_token) {
          holdUser(user);
          return "ok";
        }
        return "unreachable";
      } catch (error) {
        const verdict = classify(error);
        if (verdict === "unreachable") return verdict;
        // A refusal is believed only once storage has no newer session than the one refused.
        const after = await manager.getUser().catch(() => null);
        if (after && after.refresh_token && after.refresh_token !== before.refresh_token && usable(after)) {
          holdUser(after);
          return "ok";
        }
        return verdict;
      }
    });
  }

  /** @returns {Promise<Outcome>} */
  async function attemptAdopted() {
    const source = adopted;
    try {
      const next = await source.renew();
      if (adopted !== source) return "ok";
      if (!next) return "refused";
      holdAdopted(next);
      return "ok";
    } catch {
      return "unreachable";
    }
  }

  /** Settles what an attempt found: nothing for a success, a retry for silence, a grace for a refusal. */
  function settle(outcome) {
    if (snapshot.status === "ended") return;
    switch (outcome) {
      case "ok":
        return;
      case "gone":
        endNow("revoked");
        return;
      case "unreachable":
        if (refusal) refusal.final = false;
        if (token) publish("stale");
        scheduleRetry();
        return;
      default: {
        const reason = outcome === "denied" ? "denied" : "refused";
        if (refusal?.final || booting) {
          endNow(reason);
          return;
        }
        refusal = { reason, final: false };
        if (token) publish("stale");
        endTimer ??= setTimeout(graceOver, graceMs);
      }
    }
  }

  function graceOver() {
    endTimer = null;
    if (!refusal) return;
    refusal.final = true;
    void renew();
  }

  /** One renewal, shared by every caller in this tab. @returns {Promise<Outcome>} */
  function renew() {
    renewing ??= (async () => {
      let outcome;
      try {
        outcome = await attempt();
      } catch {
        outcome = "unreachable";
      }
      settle(outcome);
      return outcome;
    })().finally(() => { renewing = null; });
    return renewing;
  }

  // ---------------------------------------------------------------- the browser

  function attachListeners() {
    if (listening || typeof window === "undefined") return;
    listening = true;
    window.addEventListener("online", nudge);
    window.addEventListener("storage", onStorage);
    document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") nudge(); });
  }

  /** The network is back or the page is visible again: retry now what was waiting on a clock. */
  function nudge() {
    if (refusal || renewing) return;
    if (snapshot.status === "stale") {
      clearTimeout(retryTimer);
      retryTimer = null;
      void renew();
    } else if (snapshot.status === "live" && expiresAt != null && expiresAt - Date.now() <= renewAheadMs) {
      void renew();
    }
  }

  function onStorage(event) {
    if (event.storageArea !== globalThis.localStorage) return;
    if (event.key && !event.key.startsWith(prefix)) return;
    void syncFromStorage();
  }

  /** Another tab changed the stored session: take its newer one, or end with it. */
  async function syncFromStorage() {
    if (adopted || !manager || snapshot.status === "restoring") return;
    const user = await manager.getUser().catch(() => null);
    if (!user) {
      if (snapshot.status === "live" || snapshot.status === "stale") endNow("revoked");
      return;
    }
    if (user.access_token !== token && usable(user)) holdUser(user);
  }

  // ---------------------------------------------------------------- the interface

  function gate() {
    if (snapshot.status === "ended") throw new SessionError("ended");
    if (snapshot.status === "none") throw new SessionError("none");
  }

  const tokenGood = () => expiresAt != null && expiresAt - Date.now() >= renewAheadMs;

  /** @param {{ force?: boolean }} [options] */
  async function fresh(options = {}) {
    if (restoring && snapshot.status === "restoring") await restoring;
    gate();
    // `force`: a server refused the token in hand, on a request that cannot be replayed through
    // authorizedFetch (a stream, a worker's upload); renew whatever its expiry says.
    if ((options.force || !tokenGood()) && !refusal) {
      await renew();
      gate();
    }
    return token;
  }

  /** The token held now, for a request that cannot wait on a promise; null without a session. */
  function currentToken() {
    return snapshot.status === "live" || snapshot.status === "stale" ? token : null;
  }

  async function authorizedFetch(input, init) {
    const { fresh: forceFresh, ...rest } = init || {};
    if (forceFresh && !refusal) await renew();
    const sent = await fresh();

    const send = (bearer) => {
      const headers = new Headers(rest.headers || (input instanceof Request ? input.headers : undefined));
      headers.set("Authorization", `Bearer ${bearer}`);
      return fetch(input instanceof Request ? input.clone() : input, { ...rest, headers });
    };

    const response = await send(sent);
    if (response.status !== 401 || refusal) return response;
    // Another call may have renewed already; otherwise renew once, and replay once.
    if (token && token !== sent && tokenGood()) return send(token);
    const outcome = await renew();
    if (outcome !== "ok" || !token || token === sent) return response;
    return send(token);
  }

  function restore() {
    restoring ??= doRestore().finally(() => { restoring = null; });
    return restoring;
  }

  /** @returns {Promise<string | null>} */
  async function doRestore() {
    if (adopted) return null;
    if (snapshot.status === "none") publish("restoring");
    try {
      await init();
    } catch {
      if (snapshot.status === "restoring") publish("none");
      return null;
    }
    const query = new URLSearchParams(globalThis.location.search);
    if (globalThis.location.pathname === redirectPath && (query.has("code") || query.has("error"))) {
      return completeSignIn();
    }
    const user = await manager.getUser().catch(() => null);
    if (!user || !user.access_token) {
      publish("none");
      return null;
    }
    if (usable(user)) {
      holdUser(user);
      return null;
    }
    setTokens(user.access_token, expiryOfUser(user), user.profile);
    booting = true;
    try {
      await renew();
    } finally {
      booting = false;
    }
    if (snapshot.status === "restoring") publish("stale");
    return null;
  }

  /** @returns {Promise<string | null>} The path the sign-in carried. */
  async function completeSignIn() {
    const href = globalThis.location.href;
    globalThis.history.replaceState(null, "", redirectPath);
    try {
      const user = await manager.signinRedirectCallback(href);
      if (!user || !user.access_token) {
        publish("none");
        return null;
      }
      removeSession(`${prefix}callback-failed`);
      holdUser(user);
      return safeReturnTo(/** @type {any} */ (user.state)?.returnTo);
    } catch (error) {
      if (error instanceof ErrorResponse && error.error === "access_denied") {
        endNow("denied");
        return null;
      }
      // A callback that fails once (a code already spent by a reload or the back button) signs in
      // again without a word; one that fails again within a minute would loop, so it ends.
      const key = `${prefix}callback-failed`;
      const last = Number(readSession(key)) || 0;
      if (Date.now() - last < CALLBACK_RETRY_WINDOW_MS) {
        removeSession(key);
        endNow("refused");
      } else {
        writeSession(key, String(Date.now()));
        publish("none");
      }
      return null;
    }
  }

  /** @returns {Promise<boolean>} Whether the browser was sent to the provider. */
  async function signIn(options = {}) {
    const loc = globalThis.location;
    const returnTo = options.returnTo ?? loc.pathname + loc.search + loc.hash;
    try {
      await init();
      await manager.signinRedirect({ state: { returnTo } });
      return true;
    } catch {
      return false;
    }
  }

  async function signOut() {
    clearTimeout(timer);
    timer = null;
    const wasAdopted = adopted;
    adopted = null;
    let user = null;
    if (!wasAdopted) {
      try {
        await init();
        user = await manager.getUser();
      } catch { /* nothing stored, or no provider to ask */ }
    }
    recovered();
    clearTokens();
    publish("none");
    if (wasAdopted || !manager) return;
    if (user && user.id_token) {
      await manager.removeUser().catch(() => {});
      try {
        await manager.signoutRedirect({ id_token_hint: user.id_token });
        return;
      } catch {
        globalThis.location.assign(postSignOutPath);
        return;
      }
    }
    // A device session has no id token: it signs out by revoking its refresh token.
    if (user && user.refresh_token) await manager.revokeTokens(["refresh_token"]).catch(() => {});
    await manager.removeUser().catch(() => {});
  }

  function adopt(tokens, renewFn) {
    adopted = { renew: renewFn };
    holdAdopted(tokens);
  }

  function end(reason = "revoked") {
    if (!REASONS.has(reason)) throw new TypeError(`Unknown end reason: ${reason}`);
    endNow(reason);
  }

  function updateAccount(patch) {
    if (!snapshot.account) return;
    accountPatch = { ...accountPatch, ...patch };
    publish(snapshot.status, snapshot.reason);
  }

  const device = {
    start: (options) =>
      startDevice(
        {
          init,
          async adoptUser(user) {
            holdUser(user);
          },
        },
        options,
      ),
  };

  return {
    restore,
    signIn,
    fresh,
    token: currentToken,
    authorizedFetch,
    signOut,
    adopt,
    end,
    updateAccount,
    device,
    subscribe(listener) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    getSnapshot: () => snapshot,
  };
}
