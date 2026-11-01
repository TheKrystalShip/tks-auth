import { afterEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { ApiError, createApi, failureOf, readLines } from "../src/api.js";
import { createStandInSession } from "../src/testing.js";
import { useSession } from "../src/react.js";

const json = (status, body, headers = {}) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });

/** A session whose `authorizedFetch` answers from `answer`, recording each request. */
function sessionAnswering(answer, status = "live") {
  const sent = [];
  let current = status;
  return {
    sent,
    ended: [],
    getSnapshot: () => ({ status: current }),
    end(reason) { this.ended.push(reason); current = "ended"; },
    authorizedFetch: async (url, init) => {
      sent.push({ url, init });
      return answer(url, init);
    },
  };
}

const failing = (code) => async () => { throw Object.assign(new Error(code), { code }); };

afterEach(() => vi.unstubAllGlobals());

describe("failureOf", () => {
  it.each([
    [{ error: { code: "forbidden", message: "Not yours." } }, { message: "Not yours.", code: "forbidden" }],
    [{ error: "invalid-shop", detail: "No such shop." }, { message: "No such shop.", code: "invalid-shop" }],
    [{ error: "Sign in to Cinema." }, { message: "Sign in to Cinema.", code: null }],
    [{ title: "Bad Request", detail: "The id is malformed." }, { message: "The id is malformed.", code: null }],
    [{ message: "Too many." }, { message: "Too many.", code: null }],
    [{ title: "Bad Request" }, { message: "Bad Request", code: null }],
    [null, { message: null, code: null }],
    ["text", { message: null, code: null }],
  ])("reads %j", (body, expected) => {
    expect(failureOf(body)).toEqual(expected);
  });
});

describe("createApi", () => {
  it("sends JSON to the base with the site's headers and returns the answer", async () => {
    const session = sessionAnswering(() => json(200, { id: 1 }));
    const api = createApi({ session, base: "https://api.test/v1/", headers: () => ({ "Accept-Language": "ca" }) });

    expect(await api.post("/things", { name: "x" })).toEqual({ id: 1 });

    const { url, init } = session.sent[0];
    expect(url).toBe("https://api.test/v1/things");
    expect(init.method).toBe("POST");
    expect(init.headers).toMatchObject({ Accept: "application/json", "Content-Type": "application/json", "Accept-Language": "ca" });
    expect(init.body).toBe('{"name":"x"}');
  });

  it("sends the credentials the site names", async () => {
    const session = sessionAnswering(() => json(200, {}));

    await createApi({ session, credentials: "omit" }).get("/x");

    expect(session.sent[0].init.credentials).toBe("omit");
  });

  it("answers null for a 204, and sends a form as it is", async () => {
    const session = sessionAnswering(() => new Response(null, { status: 204 }));
    const api = createApi({ session });
    const form = new FormData();

    expect(await api.upload("/photo", form)).toBeNull();
    expect(session.sent[0].init.body).toBe(form);
    expect(session.sent[0].init.headers["Content-Type"]).toBeUndefined();
  });

  it("throws the answer's own sentence and code, with its status and body", async () => {
    const body = { error: "too-many-starred", detail: "A person stars at most 5 shops." };
    const api = createApi({ session: sessionAnswering(() => json(429, body)) });

    const failure = await api.get("/starred").catch((e) => e);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 429, message: body.detail, code: "too-many-starred", body });
  });

  it("says the status line when the answer has no words of its own, or the site's own sentence", async () => {
    const answer = () => new Response("oops", { status: 502, statusText: "Bad Gateway" });

    await expect(createApi({ session: sessionAnswering(answer) }).get("/x")).rejects.toMatchObject({ status: 502, message: "Bad Gateway" });
    await expect(createApi({ session: sessionAnswering(answer), words: { failed: (s) => `Answered ${s}.` } }).get("/x"))
      .rejects.toMatchObject({ status: 502, message: "Answered 502." });
  });

  it("is a status-0 failure when no server answers, and says so to the site", async () => {
    const reach = [];
    const api = createApi({ session: { authorizedFetch: failing(undefined) }, onReach: (r) => reach.push(r) });

    await expect(api.get("/x")).rejects.toMatchObject({ status: 0, message: "The API cannot be reached." });
    expect(reach).toEqual([false]);
  });

  it("is a status-0 failure when something other than the site's server answered", async () => {
    const reach = [];
    const api = createApi({
      session: sessionAnswering(() => json(200, {}, { "X-Portal": "1" })),
      answered: (r) => !r.headers.get("X-Portal"),
      onReach: (r) => reach.push(r),
    });

    await expect(api.get("/x")).rejects.toMatchObject({ status: 0 });
    expect(reach).toEqual([false]);
  });

  it("is a sign-in failure when the session has ended or there is none", async () => {
    const api = createApi({ session: { authorizedFetch: failing("ended") } });

    await expect(api.get("/x")).rejects.toMatchObject({ status: 401, message: "Sign in to carry on." });
  });

  it("sends without a bearer when nobody is signed in, for a site that serves anyone", async () => {
    const plain = vi.fn(async () => json(200, { open: true }));
    vi.stubGlobal("fetch", plain);
    const api = createApi({ session: { authorizedFetch: failing("none") }, anonymous: true });

    expect(await api.get("/open")).toEqual({ open: true });
    expect(plain).toHaveBeenCalledOnce();
  });

  it("ends a live session on a refusal its renewal did not cure, when the site asks", async () => {
    const session = sessionAnswering(() => json(401, { error: "Sign in." }));
    const api = createApi({ session, endOnRefusal: true, words: { signIn: "Sign in again." } });

    await expect(api.get("/me")).rejects.toMatchObject({ status: 401, message: "Sign in again." });
    expect(session.ended).toEqual(["refused"]);
  });

  it("leaves the session alone on a refusal otherwise", async () => {
    const session = sessionAnswering(() => json(401, { error: "Sign in." }));
    const api = createApi({ session });

    await expect(api.get("/me")).rejects.toMatchObject({ status: 401, message: "Sign in." });
    expect(session.ended).toEqual([]);
  });

  it("says no API is configured when the base it asks for is empty", async () => {
    const session = sessionAnswering(() => json(200, {}));
    const api = createApi({ session, base: () => "" });

    await expect(api.get("/x")).rejects.toMatchObject({ status: 0, message: "No API is configured." });
    expect(session.sent).toEqual([]);
  });

  it("streams each line of an ndjson answer as it arrives", async () => {
    const lines = [];
    const api = createApi({ session: sessionAnswering(() => new Response('{"a":1}\n{"a":2}\n{"a":3}', { status: 200 })) });

    await api.stream("/import", { url: "u" }, (line) => lines.push(line.a));

    expect(lines).toEqual([1, 2, 3]);
  });

  it("serves a file as a blob", async () => {
    const api = createApi({ session: sessionAnswering(() => new Response("bytes", { status: 200 })) });

    expect(await (await api.blob("/photo")).text()).toBe("bytes");
  });
});

describe("readLines", () => {
  it("joins a line split across chunks", async () => {
    const chunks = ['{"a"', ':1}\n{"a":', "2}"];
    const body = new ReadableStream({
      start(c) { for (const chunk of chunks) c.enqueue(new TextEncoder().encode(chunk)); c.close(); },
    });
    const seen = [];

    await readLines(body, (line) => seen.push(line.a));

    expect(seen).toEqual([1, 2]);
  });
});

describe("createStandInSession", () => {
  it("draws what a test sets, through the package's hook", () => {
    const session = createStandInSession();
    const { result } = renderHook(() => useSession(session));
    expect(result.current.status).toBe("none");

    act(() => session.adopt({ claims: { sub: "u-1", name: "Ada" } }));
    expect(result.current).toMatchObject({ status: "live", account: { id: "u-1", name: "Ada" } });

    act(() => session.end("revoked"));
    expect(result.current).toMatchObject({ status: "ended", reason: "revoked" });
  });

  it("offers no token", async () => {
    const session = createStandInSession();

    await expect(session.fresh()).rejects.toMatchObject({ code: "none" });
    expect(session.token()).toBeNull();
  });
});
