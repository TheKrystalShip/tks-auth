import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { UserManager } from "oidc-client-ts";
import { createSession } from "../src/index.js";
import { API, CLIENT_ID, ISSUER, PREFIX, cfg, createAnchor, installLocks, jwt, removeLocks, seed, storedUser } from "./anchor.js";

let anchor;
const tick = (ms) => vi.advanceTimersByTimeAsync(ms);

beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  history.replaceState(null, "", "/");
  anchor = createAnchor();
  anchor.install();
  installLocks();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  removeLocks();
});

/** A session restored from what a past sign-in stored. */
async function live(options = {}) {
  vi.useFakeTimers();
  const tokens = await seed(anchor);
  const session = createSession({ ...cfg, ...options });
  await session.restore();
  expect(session.getSnapshot().status).toBe("live");
  return { session, tokens };
}

describe("sign-in", () => {
  it("redeems the code on the callback path and returns the path it carried", async () => {
    const leaving = createSession(cfg);
    void leaving.signIn({ returnTo: "/films?q=1#top" });
    await vi.waitFor(() => expect(Object.keys(sessionStorage).some((k) => k.startsWith(PREFIX))).toBe(true));
    const stateKey = Object.keys(sessionStorage).find((k) => k.startsWith(PREFIX));
    const stateId = stateKey.slice(PREFIX.length);
    anchor.codes.set("abc", "u1");

    history.pushState(null, "", `/signed-in?code=abc&state=${stateId}`);
    const returned = createSession(cfg);
    const returnTo = await returned.restore();

    expect(returnTo).toBe("/films?q=1#top");
    expect(location.pathname + location.search).toBe("/signed-in");
    const snap = returned.getSnapshot();
    expect(snap.status).toBe("live");
    expect(snap.account).toEqual({ id: "u1", name: "Ada", username: "ada" });
    expect(snap.claims.sub).toBe("u1");
    expect(storedUser().refresh_token).toMatch(/^r/);
    expect(anchor.calls.code).toBe(1);
  });

  it("is signed out with nothing stored, and ended with access_denied", async () => {
    const none = createSession(cfg);
    await none.restore();
    expect(none.getSnapshot().status).toBe("none");

    const leaving = createSession(cfg);
    void leaving.signIn({ returnTo: "/" });
    await vi.waitFor(() => expect(Object.keys(sessionStorage).some((k) => k.startsWith(PREFIX))).toBe(true));
    const stateId = Object.keys(sessionStorage).find((k) => k.startsWith(PREFIX)).slice(PREFIX.length);
    history.pushState(null, "", `/signed-in?error=access_denied&state=${stateId}`);
    const denied = createSession(cfg);
    await denied.restore();
    expect(denied.getSnapshot()).toMatchObject({ status: "ended", reason: "denied" });
  });

  it("signs in again once after a failed callback, and ends when it fails again", async () => {
    history.pushState(null, "", "/signed-in?code=spent&state=nothing-stored");
    const first = createSession(cfg);
    await first.restore();
    expect(first.getSnapshot().status).toBe("none");

    history.pushState(null, "", "/signed-in?code=spent&state=nothing-stored");
    const second = createSession(cfg);
    await second.restore();
    expect(second.getSnapshot()).toMatchObject({ status: "ended", reason: "refused" });
  });

  it("keeps only same-origin paths as a return-to", async () => {
    const leaving = createSession(cfg);
    void leaving.signIn({ returnTo: "//evil.test/x" });
    await vi.waitFor(() => expect(Object.keys(sessionStorage).some((k) => k.startsWith(PREFIX))).toBe(true));
    const stateId = Object.keys(sessionStorage).find((k) => k.startsWith(PREFIX)).slice(PREFIX.length);
    anchor.codes.set("abc", "u1");
    history.pushState(null, "", `/signed-in?code=abc&state=${stateId}`);
    expect(await createSession(cfg).restore()).toBeNull();
  });
});

describe("renewal", () => {
  it("renews on a timer ahead of expiry, with nothing ended or stale on the way", async () => {
    const { session, tokens } = await live();
    const seen = [];
    session.subscribe(() => seen.push(session.getSnapshot().status));

    await tick((300 - 60) * 1000 - 1000);
    expect(anchor.calls.refresh).toBe(0);
    await tick(2000);

    expect(anchor.calls.refresh).toBe(1);
    const snap = session.getSnapshot();
    expect(snap.status).toBe("live");
    expect(snap.expiresAt).toBeGreaterThan(Date.now() + 200 * 1000);
    expect(await session.fresh()).not.toBe(tokens.access_token);
    expect(seen.every((s) => s === "live")).toBe(true);
  });

  it("renews on a 401 and replays the call once", async () => {
    const { session, tokens } = await live();
    anchor.invalidate(tokens.access_token);

    const response = await session.authorizedFetch(API);

    expect(response.status).toBe(200);
    expect(anchor.calls.api).toBe(2);
    expect(anchor.calls.refresh).toBe(1);
  });

  it("replays a refused call only once", async () => {
    const { session } = await live();
    anchor.apiAlways401 = true;

    const response = await session.authorizedFetch(API);

    expect(response.status).toBe(401);
    expect(anchor.calls.api).toBe(2);
    expect(anchor.calls.refresh).toBe(1);
  });

  it("renews before a call when asked for a fresh token", async () => {
    const { session, tokens } = await live();
    const response = await session.authorizedFetch(API, { fresh: true });
    expect(response.status).toBe(200);
    expect(anchor.calls.refresh).toBe(1);
    expect(anchor.calls.api).toBe(1);
    expect(await session.fresh()).not.toBe(tokens.access_token);
  });

  it("shares one renewal among concurrent callers of a tab", async () => {
    removeLocks();
    const { session, tokens } = await live();
    anchor.invalidate(tokens.access_token);
    const results = await Promise.all([session.authorizedFetch(API), session.authorizedFetch(API), session.fresh()]);
    expect(results[0].status).toBe(200);
    expect(results[1].status).toBe(200);
    expect(anchor.calls.refresh).toBe(1);
  });

  it("renews once for two sessions on one storage, under the lock, with one refresh token", async () => {
    vi.useFakeTimers();
    const tokens = await seed(anchor);
    const a = createSession(cfg);
    const b = createSession(cfg);
    await Promise.all([a.restore(), b.restore()]);
    anchor.invalidate(tokens.access_token);

    const [ra, rb] = await Promise.all([a.authorizedFetch(API), b.authorizedFetch(API)]);

    expect(ra.status).toBe(200);
    expect(rb.status).toBe(200);
    expect(anchor.calls.refresh).toBe(1);
    expect(a.getSnapshot().status).toBe("live");
    expect(b.getSnapshot().status).toBe("live");
    expect(a.getSnapshot().claims.jti).toBe(b.getSnapshot().claims.jti);
  });

  it("finds a newer token in storage when a refusal arrives, and does not end", async () => {
    const { session } = await live();
    const events = [];
    session.subscribe(() => events.push(session.getSnapshot().status));
    // While this tab's refresh is in flight, another tab stores a newer session.
    anchor.beforeRefresh = async () => {
      anchor.beforeRefresh = null;
      await seed(anchor);
      anchor.refuseAll = true;
    };

    await tick(250 * 1000);
    await tick(60 * 1000);

    expect(session.getSnapshot().status).toBe("live");
    expect(events).not.toContain("ended");
    expect(session.getSnapshot().claims.jti).toBe(JSON.parse(atob(storedUser().access_token.split(".")[1])).jti);
  });
});

describe("refusal", () => {
  it("ends only after the grace, with the reason", async () => {
    const { session } = await live({ graceMs: 30000 });
    anchor.refuseAll = true;

    await tick(241 * 1000);
    expect(session.getSnapshot()).toMatchObject({ status: "stale", reason: null });
    await tick(28000);
    expect(session.getSnapshot().status).toBe("stale");
    await tick(2000);

    expect(session.getSnapshot()).toMatchObject({ status: "ended", reason: "refused", account: null });
    expect(storedUser()).toBeNull();
    await expect(session.fresh()).rejects.toMatchObject({ code: "ended" });
  });

  it("recovers when the refusal clears inside the grace", async () => {
    const { session } = await live();
    anchor.refuseAll = true;
    await tick(250 * 1000);
    expect(session.getSnapshot().status).toBe("stale");
    // Another tab signs in again inside the grace.
    await seed(anchor);
    window.dispatchEvent(new StorageEvent("storage", { storageArea: localStorage, key: `${PREFIX}user:${ISSUER}:${CLIENT_ID}` }));
    await tick(0);
    await vi.waitFor(() => expect(session.getSnapshot().status).toBe("live"));
    await tick(60 * 1000);
    expect(session.getSnapshot().status).toBe("live");
  });

  it("reports access_denied as denied", async () => {
    const { session } = await live({ graceMs: 1000 });
    anchor.refuseAll = true;
    anchor.refuseWith = "access_denied";
    await tick(250 * 1000);
    await tick(1500);
    expect(session.getSnapshot()).toMatchObject({ status: "ended", reason: "denied" });
  });

  it("does not end on a refusal while the anchor stays unreachable at the grace's end", async () => {
    const { session } = await live({ graceMs: 5000 });
    anchor.refuseAll = true;
    await tick(241 * 1000);
    anchor.down = true;
    await tick(6000);
    expect(session.getSnapshot().status).toBe("stale");
  });
});

describe("outages", () => {
  it("goes stale on a network failure, retries with backoff, and is live again on reconnect", async () => {
    const { session } = await live();
    const seen = [];
    session.subscribe(() => seen.push(session.getSnapshot().status));
    anchor.down = true;

    await tick(250 * 1000);
    expect(session.getSnapshot().status).toBe("stale");
    const first = anchor.calls.refresh;
    await tick(14000);
    expect(anchor.calls.refresh).toBe(first);
    await tick(1500);
    expect(anchor.calls.refresh).toBe(first);   // the anchor is down: the request never arrives
    expect(session.getSnapshot().status).toBe("stale");

    anchor.down = false;
    window.dispatchEvent(new Event("online"));
    await vi.waitFor(() => expect(session.getSnapshot().status).toBe("live"));
    expect(anchor.calls.refresh).toBe(first + 1);
    expect(seen).not.toContain("ended");
  });

  it("retries by itself when nothing signals the reconnect", async () => {
    const { session } = await live();
    anchor.down = true;
    await tick(250 * 1000);
    expect(session.getSnapshot().status).toBe("stale");
    anchor.down = false;
    await tick(16000);
    expect(session.getSnapshot().status).toBe("live");
  });

  it("offers the stored token while it is unexpired, and the held one when it is not", async () => {
    const { session, tokens } = await live();
    anchor.down = true;
    await tick(250 * 1000);
    expect(session.getSnapshot().status).toBe("stale");
    expect(await session.fresh()).toBe(tokens.access_token);
    await tick(120 * 1000);
    expect(session.getSnapshot().status).toBe("stale");
    expect(await session.fresh()).toBe(tokens.access_token);
  });

  it("ends at once at boot when the refusal survives the lock and the recheck", async () => {
    vi.useFakeTimers();
    await seed(anchor);
    await tick(400 * 1000);
    anchor.refuseAll = true;
    const session = createSession(cfg);
    await session.restore();
    expect(session.getSnapshot()).toMatchObject({ status: "ended", reason: "refused" });
    expect(storedUser()).toBeNull();
  });

  it("restores an expired stored session as stale when the anchor is unreachable", async () => {
    vi.useFakeTimers();
    await seed(anchor);
    await tick(400 * 1000);
    anchor.down = true;
    const session = createSession(cfg);
    await session.restore();
    expect(session.getSnapshot().status).toBe("stale");
    expect(session.getSnapshot().account.id).toBe("u1");
    anchor.down = false;
    window.dispatchEvent(new Event("online"));
    await vi.waitFor(() => expect(session.getSnapshot().status).toBe("live"));
  });
});

describe("device grant", () => {
  const start = async (options = {}) => {
    vi.useFakeTimers();
    const session = createSession({ ...cfg, ...options });
    const code = await session.device.start({ name: "Living room" });
    return { session, code };
  };

  it("sends the device name and returns the code to show", async () => {
    const { code } = await start();
    expect(anchor.deviceForms[0]).toMatchObject({ client_id: CLIENT_ID, device_name: "Living room" });
    expect(code).toMatchObject({ userCode: "ABCD-EFGH", expiresIn: 600 });
    expect(code.verificationUriComplete).toContain("user_code");
    code.cancel();
    expect(await code.poll()).toBe("cancelled");
  });

  it("polls through pending and slow_down, then signs in and stores the session", async () => {
    const { session, code } = await start();
    anchor.deviceScript = ["authorization_pending", "slow_down", "approve"];
    const outcome = code.poll();
    await tick(5000);
    await tick(5000);
    await tick(5000);
    expect(anchor.calls.devicePoll).toBe(2);   // after slow_down the next poll waits 10 s
    await tick(5000);
    expect(await outcome).toBe("signed-in");
    expect(anchor.pollTimes[2] - anchor.pollTimes[1]).toBe(10000);
    expect(session.getSnapshot()).toMatchObject({ status: "live", account: { id: "u1" } });
    expect(storedUser().refresh_token).toBeTruthy();
    expect(storedUser().id_token).toBeUndefined();

    const restored = createSession(cfg);
    await restored.restore();
    expect(restored.getSnapshot().status).toBe("live");
  });

  it("resolves refused and expired", async () => {
    let { code } = await start();
    anchor.deviceScript = ["access_denied"];
    let outcome = code.poll();
    await tick(5000);
    expect(await outcome).toBe("refused");

    ({ code } = await start());
    anchor.deviceScript = ["expired_token"];
    outcome = code.poll();
    await tick(5000);
    expect(await outcome).toBe("expired");
  });

  it("expires by the clock when the anchor keeps saying pending", async () => {
    anchor.deviceExpiresIn = 12;
    const { code } = await start();
    const outcome = code.poll();
    await tick(15000);
    expect(await outcome).toBe("expired");
  });

  it("keeps polling through network failures", async () => {
    const { session, code } = await start();
    anchor.deviceScript = ["network", "network", "approve"];
    const outcome = code.poll();
    await tick(15000);
    expect(await outcome).toBe("signed-in");
    expect(session.getSnapshot().status).toBe("live");
  });

  it("renews a device session like any other", async () => {
    const { session, code } = await start();
    anchor.deviceScript = ["approve"];
    const outcome = code.poll();
    await tick(5000);
    expect(await outcome).toBe("signed-in");
    await tick(250 * 1000);
    expect(anchor.calls.refresh).toBe(1);
    expect(session.getSnapshot().status).toBe("live");
  });

  it("signs a device out by revoking its refresh token", async () => {
    const { session, code } = await start();
    anchor.deviceScript = ["approve"];
    const outcome = code.poll();
    await tick(5000);
    await outcome;
    const refreshToken = storedUser().refresh_token;
    const redirect = vi.spyOn(UserManager.prototype, "signoutRedirect");

    await session.signOut();

    expect(anchor.revoked).toEqual([refreshToken]);
    expect(redirect).not.toHaveBeenCalled();
    expect(session.getSnapshot().status).toBe("none");
    expect(storedUser()).toBeNull();
  });
});

describe("sign-out", () => {
  it("clears the session and redirects to the anchor with the id token", async () => {
    const { session, tokens } = await live();
    const redirect = vi.spyOn(UserManager.prototype, "signoutRedirect").mockResolvedValue();
    await session.signOut();
    expect(redirect).toHaveBeenCalledWith({ id_token_hint: tokens.id_token });
    expect(session.getSnapshot().status).toBe("none");
    expect(storedUser()).toBeNull();
    expect(anchor.calls.revoke).toBe(0);
  });

  it("ends a tab when another tab signs the session out", async () => {
    const { session } = await live();
    localStorage.clear();
    window.dispatchEvent(new StorageEvent("storage", { storageArea: localStorage, key: null }));
    await vi.waitFor(() => expect(session.getSnapshot()).toMatchObject({ status: "ended", reason: "revoked" }));
  });
});

describe("adopt", () => {
  it("holds a session minted elsewhere and renews it through the site's function", async () => {
    vi.useFakeTimers();
    const session = createSession(cfg);
    const mint = (sub) => jwt({ sub, name: "Grace", exp: Math.floor(Date.now() / 1000) + 120, jti: Math.random() });
    const renew = vi.fn(async () => ({ access_token: mint("d1"), expires_in: 120 }));

    session.adopt({ access_token: mint("d1"), expires_in: 120 }, renew);
    expect(session.getSnapshot()).toMatchObject({ status: "live", account: { id: "d1", name: "Grace" } });
    const first = session.getSnapshot().claims.jti;

    await tick(61 * 1000);
    expect(renew).toHaveBeenCalledTimes(1);
    expect(session.getSnapshot().claims.jti).not.toBe(first);
    expect(session.getSnapshot().status).toBe("live");
    expect(localStorage.length).toBe(0);
  });

  it("goes stale when the site's renewal throws, retries, and ends only when it says there is no more", async () => {
    vi.useFakeTimers();
    const session = createSession({ ...cfg, graceMs: 5000 });
    const mint = () => jwt({ sub: "d1", exp: Math.floor(Date.now() / 1000) + 120, jti: Math.random() });
    let mode = "throw";
    const renew = vi.fn(async () => {
      if (mode === "throw") throw new Error("offline");
      if (mode === "none") return null;
      return { access_token: mint(), expires_in: 120 };
    });
    session.adopt({ access_token: mint(), expires_in: 120 }, renew);

    await tick(61 * 1000);
    expect(session.getSnapshot().status).toBe("stale");
    mode = "ok";
    await tick(15000);
    expect(session.getSnapshot().status).toBe("live");

    mode = "none";
    await tick(61 * 1000);
    expect(session.getSnapshot().status).toBe("stale");
    await tick(5500);
    expect(session.getSnapshot()).toMatchObject({ status: "ended", reason: "refused" });
  });

  it("signs out without touching storage or the anchor", async () => {
    const session = createSession(cfg);
    session.adopt({ access_token: jwt({ sub: "d1", exp: Math.floor(Date.now() / 1000) + 120 }), expires_in: 120 }, async () => null);
    await session.signOut();
    expect(session.getSnapshot().status).toBe("none");
    expect(anchor.calls.revoke).toBe(0);
  });
});

describe("snapshot", () => {
  it("is the same object until something changes, and notifies only on change", async () => {
    const { session } = await live();
    const first = session.getSnapshot();
    const listener = vi.fn();
    session.subscribe(listener);
    expect(session.getSnapshot()).toBe(first);

    session.updateAccount({});
    expect(session.getSnapshot()).toBe(first);
    expect(listener).not.toHaveBeenCalled();

    session.updateAccount({ name: "Ada L." });
    expect(session.getSnapshot()).not.toBe(first);
    expect(session.getSnapshot().account).toEqual({ id: "u1", name: "Ada L.", username: "ada" });
    expect(listener).toHaveBeenCalledTimes(1);

    await tick(250 * 1000);
    expect(session.getSnapshot().account.name).toBe("Ada L.");
    expect(session.getSnapshot().claims).not.toBe(first.claims);
  });

  it("ends on the site's say-so and offers no token after", async () => {
    const { session } = await live();
    session.end("revoked");
    expect(session.getSnapshot()).toMatchObject({ status: "ended", reason: "revoked", account: null, expiresAt: null });
    await expect(session.fresh()).rejects.toMatchObject({ code: "ended" });
    expect(() => session.end("nonsense")).toThrow(TypeError);
  });

  it("stops notifying after unsubscribe", async () => {
    const { session } = await live();
    const listener = vi.fn();
    const off = session.subscribe(listener);
    off();
    session.end();
    expect(listener).not.toHaveBeenCalled();
  });
});

describe("discovery", () => {
  it("takes its issuer and client id from resolve", async () => {
    vi.useFakeTimers();
    await seed(anchor, { clientId: "other" });
    const session = createSession({ resolve: async () => ({ issuer: ISSUER, clientId: "other" }) });
    await session.restore();
    expect(session.getSnapshot().status).toBe("live");
  });

  it("is signed out, not thrown, when discovery fails", async () => {
    const session = createSession({ resolve: async () => { throw new Error("no network"); } });
    await expect(session.restore()).resolves.toBeNull();
    expect(session.getSnapshot().status).toBe("none");
  });

  it("keeps two clients on one origin apart", async () => {
    vi.useFakeTimers();
    await seed(anchor, { clientId: "other" });
    const session = createSession(cfg);
    await session.restore();
    expect(session.getSnapshot().status).toBe("none");
  });
});
