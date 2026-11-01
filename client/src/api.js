// A site's API over the session: bodies go in and come out as JSON, the session's `authorizedFetch`
// carries the bearer (renewing and replaying a refused call once), and every failure is an `ApiError`
// whose message is fit to show.

/** A call that did not succeed: `status` is 0 when no server answered. */
export class ApiError extends Error {
  /**
   * @param {number} status
   * @param {string} message
   * @param {{ code?: string | null, body?: unknown }} [detail]
   */
  constructor(status, message, detail = {}) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    /** The answer's own code for the failure, when it names one. */
    this.code = detail.code ?? null;
    /** The parsed body of the answer, or null. */
    this.body = detail.body ?? null;
  }
}

/**
 * The sentence and code a failed answer's body carries, whichever of the organization's shapes it is:
 * `{ error: { code, message } }`, `{ error, detail }`, `{ error }`, or a problem's `{ title, detail }`.
 * @param {unknown} body
 * @returns {{ message: string | null, code: string | null }}
 */
export function failureOf(body) {
  if (!body || typeof body !== "object") return { message: null, code: null };
  const b = /** @type {Record<string, any>} */ (body);
  const text = (v) => (typeof v === "string" && v ? v : null);
  if (b.error && typeof b.error === "object") return { message: text(b.error.message), code: text(b.error.code) };
  const detail = text(b.detail);
  if (detail) return { message: detail, code: text(b.error) };
  return { message: text(b.message) ?? text(b.error) ?? text(b.title), code: null };
}

/**
 * Hands each complete line of a newline-delimited JSON body to `onLine`, parsed, as it arrives.
 * @param {ReadableStream<Uint8Array>} body
 * @param {(line: any) => void} onLine
 */
export async function readLines(body, onLine) {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let pending = "";
  const flush = (text) => { if (text.trim()) onLine(JSON.parse(text)); };
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    pending += decoder.decode(value, { stream: true });
    let end;
    while ((end = pending.indexOf("\n")) >= 0) {
      flush(pending.slice(0, end));
      pending = pending.slice(end + 1);
    }
  }
  flush(pending + decoder.decode());
}

/**
 * @typedef {object} ApiOptions
 * @property {{ authorizedFetch: (input: string, init?: RequestInit) => Promise<Response>, end?: (reason: string) => void, getSnapshot?: () => { status: string } }} session
 * @property {string | (() => string | null | undefined)} [base] Prefixed to every path; a function is asked on each call, and an empty answer is "no API is configured".
 * @property {() => Record<string, string>} [headers] Added to every request, asked on each call (a language, a device id).
 * @property {RequestCredentials} [credentials] The requests' `credentials`, such as `omit` on an origin whose cookie must not ride along.
 * @property {boolean} [anonymous] Nobody signed in sends the call without a bearer, instead of failing it.
 * @property {boolean} [endOnRefusal] A 401 the session's renewal did not cure ends a live session as `refused`.
 * @property {(response: Response) => boolean} [answered] Whether a response came from the site's own server and not a gateway or a portal; one that did not is a status-0 failure.
 * @property {(reached: boolean) => void} [onReach] Told after each call whether the server answered.
 * @property {{ unreachable?: string, unconfigured?: string, signIn?: string, failed?: (status: number, statusText: string) => string }} [words] The sentences a failure with no body of its own says.
 */

/**
 * The site's API.
 * @param {ApiOptions} options
 */
export function createApi(options) {
  const { session, anonymous = false, endOnRefusal = false } = options;
  if (!session || typeof session.authorizedFetch !== "function")
    throw new TypeError("createApi takes a session: { authorizedFetch }");

  const words = {
    unreachable: "The API cannot be reached.",
    unconfigured: "No API is configured.",
    signIn: "Sign in to carry on.",
    failed: (status, statusText) => statusText || `The request failed (${status}).`,
    ...options.words,
  };
  const headersOf = options.headers ?? (() => ({}));
  const answered = options.answered ?? (() => true);
  const onReach = options.onReach ?? (() => {});
  const credentials = options.credentials ? { credentials: options.credentials } : {};

  function urlOf(path) {
    const base = typeof options.base === "function" ? options.base() : options.base ?? "";
    if (typeof options.base === "function" && !base) throw new ApiError(0, words.unconfigured);
    return String(base).replace(/\/+$/, "") + path;
  }

  /** The response the server gave, or the `ApiError` that says why there is none. */
  async function send(path, request) {
    const url = urlOf(path);
    const init = { ...request, ...credentials };
    let response;
    try {
      response = await session.authorizedFetch(url, init).catch((error) => {
        if (anonymous && error && error.code === "none") return fetch(url, init);
        throw error;
      });
    } catch (error) {
      if (error && error.name === "AbortError") throw error;
      if (error && (error.code === "ended" || error.code === "none")) throw new ApiError(401, words.signIn);
      onReach(false);
      throw new ApiError(0, words.unreachable);
    }
    if (!response || !answered(response)) {
      onReach(false);
      throw new ApiError(0, words.unreachable);
    }
    onReach(true);
    if (response.status === 401 && endOnRefusal) {
      if (session.getSnapshot?.().status === "live") session.end?.("refused");
      throw new ApiError(401, words.signIn);
    }
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      const { message, code } = failureOf(body);
      throw new ApiError(response.status, message ?? words.failed(response.status, response.statusText), { code, body });
    }
    return response;
  }

  /**
   * One call: its JSON answer, null for a 204.
   * @param {string} method
   * @param {string} path
   * @param {unknown} [body] Sent as JSON, or as it is when it is a `FormData`.
   * @param {{ signal?: AbortSignal }} [init]
   */
  async function call(method, path, body, init = {}) {
    const form = typeof FormData !== "undefined" && body instanceof FormData;
    const headers = { Accept: "application/json", ...headersOf() };
    if (body !== undefined && !form) headers["Content-Type"] = "application/json";
    const response = await send(path, {
      method, headers, signal: init.signal,
      body: body === undefined || form ? /** @type {any} */ (body) : JSON.stringify(body),
    });
    return response.status === 204 ? null : response.json();
  }

  return {
    call,
    get: (path, init) => call("GET", path, undefined, init),
    post: (path, body, init) => call("POST", path, body, init),
    put: (path, body, init) => call("PUT", path, body, init),
    patch: (path, body, init) => call("PATCH", path, body, init),
    delete: (path, body, init) => call("DELETE", path, body, init),
    /** A multipart form posted to `path`. */
    upload: (path, form, init) => call("POST", path, form, init),
    /** What the API serves at `path` as a `Blob` (a photo). */
    async blob(path, init = {}) {
      const response = await send(path, { headers: headersOf(), signal: init.signal });
      return response.blob();
    },
    /**
     * POSTs `body` to a route that answers `application/x-ndjson`, handing each line to `onLine` as it arrives.
     * @param {string} path
     * @param {unknown} body
     * @param {(line: any) => void} onLine
     * @param {AbortSignal} [signal]
     */
    async stream(path, body, onLine, signal) {
      const response = await send(path, {
        method: "POST", signal,
        headers: { Accept: "application/x-ndjson", "Content-Type": "application/json", ...headersOf() },
        body: JSON.stringify(body),
      });
      await readLines(/** @type {ReadableStream<Uint8Array>} */ (response.body), onLine);
    },
  };
}
