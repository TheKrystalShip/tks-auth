import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { answeredByServer, createConnectivity } from "../src/connectivity.js";
import { createApi } from "../src/api.js";
import { useOnline } from "../src/react.js";

const json = (status, body = {}) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const html = (status) => new Response("<html></html>", { status, headers: { "Content-Type": "text/html" } });

/** Lets the probe's awaited fetch settle under fake timers. */
const settle = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe("answeredByServer", () => {
  it("counts any answer of the server's own, an error among them", () => {
    expect(answeredByServer(json(200))).toBe(true);
    expect(answeredByServer(json(500))).toBe(true);
    expect(answeredByServer(json(503))).toBe(true);
    expect(answeredByServer(new Response(null, { status: 204 }))).toBe(true);
  });

  it("refuses a gateway's page for an API that is down and a captive portal's page", () => {
    expect(answeredByServer(new Response("bad gateway", { status: 502, headers: { "Content-Type": "text/plain" } }))).toBe(false);
    expect(answeredByServer(html(200))).toBe(false);
  });
});

describe("createConnectivity", () => {
  it("starts unmeasured, which counts as online, and is measured by the first report", () => {
    const c = createConnectivity({ probe: "" });
    expect(c.getSnapshot().state).toBe("checking");
    expect(c.isOnline()).toBe(true);
    c.report(true);
    expect(c.getSnapshot().state).toBe("online");
  });

  it("starts offline when the browser says it has no network", () => {
    vi.stubGlobal("navigator", { onLine: false });
    expect(createConnectivity({ probe: "" }).getSnapshot().state).toBe("offline");
  });

  it("goes offline on a call that did not reach the server, probes on a lengthening wait, and comes back on an answer", async () => {
    const fetch = vi.fn().mockRejectedValue(new TypeError("network"));
    vi.stubGlobal("fetch", fetch);
    const c = createConnectivity({ probe: "/api/health", firstWaitMs: 1000, longestWaitMs: 4000 });
    const back = vi.fn();
    c.onReconnect(back);

    c.report(false);
    expect(c.isOnline()).toBe(false);
    await vi.advanceTimersByTimeAsync(999);
    expect(fetch).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0][0]).toBe("/api/health");
    await vi.advanceTimersByTimeAsync(2000);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(4000);
    expect(fetch).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(4000);
    expect(fetch).toHaveBeenCalledTimes(4);
    expect(back).not.toHaveBeenCalled();

    fetch.mockResolvedValue(json(200));
    await vi.advanceTimersByTimeAsync(4000);
    expect(c.getSnapshot().state).toBe("online");
    expect(back).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(60000);
    expect(fetch).toHaveBeenCalledTimes(5);
  });

  it("does not call a reconnect listener for the first measure", () => {
    const c = createConnectivity({ probe: "" });
    const back = vi.fn();
    c.onReconnect(back);
    c.report(true);
    expect(back).not.toHaveBeenCalled();
  });

  it("holds a probe answered by a captive portal as offline", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(html(200)));
    const c = createConnectivity({ probe: "/api/health" });
    expect(await c.probe()).toBe(false);
    expect(c.getSnapshot().state).toBe("offline");
  });

  it("asks a probe function each time, and with no address takes the browser's word", async () => {
    const fetch = vi.fn().mockResolvedValue(json(200));
    vi.stubGlobal("fetch", fetch);
    let base = "";
    const c = createConnectivity({ probe: () => base && base + "/health" });
    c.report(false);
    await c.probe();
    expect(fetch).not.toHaveBeenCalled();
    expect(c.getSnapshot().state).toBe("online");
    vi.stubGlobal("navigator", { onLine: false });
    await c.probe();
    expect(c.getSnapshot().state).toBe("offline");
    vi.stubGlobal("navigator", { onLine: true });
    base = "https://api.test";
    await c.probe();
    expect(fetch.mock.calls[0][0]).toBe("https://api.test/health");
  });

  it("measures once on start, believes the browser's offline event, and probes at once on its online event", async () => {
    const fetch = vi.fn().mockResolvedValue(json(200));
    vi.stubGlobal("fetch", fetch);
    const c = createConnectivity({ probe: "/api/health", firstWaitMs: 30000 });
    const stop = c.start();
    await settle();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(c.getSnapshot().state).toBe("online");

    window.dispatchEvent(new Event("offline"));
    expect(c.getSnapshot().state).toBe("offline");

    window.dispatchEvent(new Event("online"));
    await settle();
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(c.getSnapshot().state).toBe("online");

    stop();
    window.dispatchEvent(new Event("offline"));
    expect(c.getSnapshot().state).toBe("online");
  });

  it("tells its subscribers of a change and only of a change", () => {
    const c = createConnectivity({ probe: "" });
    const seen = vi.fn();
    c.subscribe(seen);
    c.report(true);
    c.report(true);
    expect(seen).toHaveBeenCalledTimes(1);
  });
});

describe("createApi with a connectivity", () => {
  it("reports every call: an answer is online, a failure on the network or a portal's page is offline", async () => {
    const c = createConnectivity({ probe: "" });
    let answer = () => json(200, { ok: true });
    const session = { authorizedFetch: async () => answer() };
    const api = createApi({ session, connectivity: c });

    await api.get("/a");
    expect(c.getSnapshot().state).toBe("online");

    answer = () => { throw new TypeError("network"); };
    await expect(api.get("/a")).rejects.toMatchObject({ status: 0 });
    expect(c.getSnapshot().state).toBe("offline");

    answer = () => json(500, { message: "broken" });
    await expect(api.get("/a")).rejects.toMatchObject({ status: 500 });
    expect(c.getSnapshot().state).toBe("online");

    answer = () => html(200);
    await expect(api.get("/a")).rejects.toMatchObject({ status: 0 });
    expect(c.getSnapshot().state).toBe("offline");
  });
});

describe("useOnline", () => {
  it("follows the connectivity", () => {
    const c = createConnectivity({ probe: "" });
    const { result } = renderHook(() => useOnline(c));
    expect(result.current).toBe(true);
    act(() => c.report(false));
    expect(result.current).toBe(false);
    act(() => c.report(true));
    expect(result.current).toBe(true);
  });
});
