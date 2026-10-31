import { User, UserManager, WebStorageStateStore } from "oidc-client-ts";
import { vi } from "vitest";

export const ISSUER = "https://auth.test";
export const API = "https://api.test/data";
export const CLIENT_ID = "site";
export const PREFIX = `tks-auth-client:${CLIENT_ID}:`;

const b64 = (value) => btoa(JSON.stringify(value)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
export const jwt = (payload) => `${b64({ alg: "none" })}.${b64(payload)}.sig`;

const json = (body, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/**
 * A fake tks-auth provider and a resource server behind one stubbed `fetch`. Refresh tokens rotate and a
 * spent one presented again ends its whole session, as the real provider does.
 */
export function createProvider({ lifetime = 300 } = {}) {
  let counter = 0;
  const refresh = new Map();          // refresh token -> { sid, sub, used }
  const deadSessions = new Set();
  const validAccess = new Set();
  const provider = {
    down: false,
    refuseAll: false,
    refuseWith: "invalid_grant",
    beforeRefresh: null,              // runs while a refresh request is in flight
    codes: new Map(),                 // code -> sub
    deviceScript: [],                 // answers for the next token polls
    deviceExpiresIn: 600,
    apiAlways401: false,
    calls: { refresh: 0, code: 0, device: 0, devicePoll: 0, revoke: 0, api: 0 },
    revoked: [],
    deviceForms: [],
    pollTimes: [],

    mint(sub = "u1", sid = `s${++counter}`, withId = true) {
      const access = jwt({ sub, name: "Ada", preferred_username: "ada", exp: Math.floor(Date.now() / 1000) + lifetime, jti: ++counter });
      validAccess.add(access);
      const refreshToken = `r${++counter}`;
      refresh.set(refreshToken, { sid, sub, used: false });
      const body = { access_token: access, refresh_token: refreshToken, token_type: "Bearer", expires_in: lifetime, scope: "openid" };
      if (withId) body.id_token = jwt({ iss: ISSUER, aud: CLIENT_ID, sub, exp: Math.floor(Date.now() / 1000) + 3600, iat: Math.floor(Date.now() / 1000) });
      return body;
    },
    invalidate(access) { validAccess.delete(access); },
    install() { vi.stubGlobal("fetch", handler); },
  };

  async function handler(input, init = {}) {
    const url = String(input instanceof Request ? input.url : input);
    const headers = new Headers(init.headers || (input instanceof Request ? input.headers : undefined));
    if (provider.down) throw new TypeError("Failed to fetch");

    if (url === API) {
      provider.calls.api += 1;
      const bearer = (headers.get("Authorization") || "").replace(/^Bearer /, "");
      if (provider.apiAlways401 || !validAccess.has(bearer)) return new Response("no", { status: 401 });
      return json({ ok: true });
    }
    if (url === `${ISSUER}/.well-known/openid-configuration`) {
      return json({
        issuer: ISSUER,
        authorization_endpoint: `${ISSUER}/authorize`,
        token_endpoint: `${ISSUER}/token`,
        end_session_endpoint: `${ISSUER}/sign-out`,
        revocation_endpoint: `${ISSUER}/revoke`,
        device_authorization_endpoint: `${ISSUER}/device_authorization`,
        jwks_uri: `${ISSUER}/jwks`,
      });
    }
    const form = new URLSearchParams(String(init.body || ""));
    if (url === `${ISSUER}/revoke`) {
      provider.calls.revoke += 1;
      provider.revoked.push(form.get("token"));
      return json({});
    }
    if (url === `${ISSUER}/device_authorization`) {
      provider.calls.device += 1;
      provider.deviceForms.push(Object.fromEntries(form));
      return json({
        device_code: "dc1", user_code: "ABCD-EFGH", verification_uri: `${ISSUER}/device`,
        verification_uri_complete: `${ISSUER}/device?user_code=ABCD-EFGH`, expires_in: provider.deviceExpiresIn, interval: 5,
      });
    }
    if (url === `${ISSUER}/token`) {
      const grant = form.get("grant_type");
      if (grant === "authorization_code") {
        provider.calls.code += 1;
        const sub = provider.codes.get(form.get("code"));
        if (!sub || !form.get("code_verifier")) return json({ error: "invalid_grant" }, 400);
        provider.codes.delete(form.get("code"));
        return json(provider.mint(sub));
      }
      if (grant === "refresh_token") {
        provider.calls.refresh += 1;
        if (provider.beforeRefresh) await provider.beforeRefresh();
        const entry = refresh.get(form.get("refresh_token"));
        if (provider.refuseAll) return json({ error: provider.refuseWith }, 400);
        if (!entry || deadSessions.has(entry.sid)) return json({ error: "invalid_grant" }, 400);
        if (entry.used) { deadSessions.add(entry.sid); return json({ error: "invalid_grant" }, 400); }
        entry.used = true;
        return json(provider.mint(entry.sub, entry.sid, !!form.get("withId") || true));
      }
      if (grant === "urn:ietf:params:oauth:grant-type:device_code") {
        provider.calls.devicePoll += 1;
        provider.pollTimes.push(Date.now());
        const answer = provider.deviceScript.shift() || "authorization_pending";
        if (answer === "approve") return json(provider.mint("u1", undefined, false));
        if (answer === "network") throw new TypeError("Failed to fetch");
        return json({ error: answer }, 400);
      }
    }
    return new Response("not found", { status: 404 });
  }

  return provider;
}

/** Stores a session for `clientId` as a past sign-in left it; returns its tokens. */
export async function seed(provider, { clientId = CLIENT_ID, withId = true, tokens } = {}) {
  const t = tokens || provider.mint("u1", undefined, withId);
  const manager = new UserManager({
    authority: ISSUER, client_id: clientId, redirect_uri: "https://app.test/signed-in",
    userStore: new WebStorageStateStore({ prefix: `tks-auth-client:${clientId}:`, store: localStorage }),
  });
  await manager.storeUser(new User({
    access_token: t.access_token, refresh_token: t.refresh_token, id_token: t.id_token, token_type: "Bearer", scope: "openid",
    expires_at: Math.floor(Date.now() / 1000) + t.expires_in,
    profile: { sub: "u1", name: "Ada", preferred_username: "ada" },
  }));
  return t;
}

export const storedUser = () => {
  const key = Object.keys(localStorage).find((k) => k.startsWith(PREFIX + "user:"));
  return key ? JSON.parse(localStorage.getItem(key)) : null;
};

/** A Web Locks stand-in: exclusive, queued per name. */
export function installLocks() {
  const tails = new Map();
  const locks = {
    request(name, fn) {
      const prev = tails.get(name) || Promise.resolve();
      const run = prev.then(() => fn());
      tails.set(name, run.catch(() => {}));
      return run;
    },
  };
  Object.defineProperty(navigator, "locks", { value: locks, configurable: true });
}
export function removeLocks() {
  Object.defineProperty(navigator, "locks", { value: undefined, configurable: true });
}

export const cfg = { issuer: ISSUER, clientId: CLIENT_ID };
