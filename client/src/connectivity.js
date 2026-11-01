// Whether the site can reach its own API: the one flag a site's pages draw their offline state by.
//
// `navigator.onLine` says only that there is no network at all: a phone on a captive portal, on a
// network whose uplink is dead, or beside a server that is down reads online. So offline is also what
// a request tells. Every call the site's API client makes reports whether the server answered
// (`createApi({ connectivity })`), and so does a probe of an address that needs no credential. A
// server that answered is reachable whatever it answered, a 500 among it; what is not the server
// answering is a gateway's 502, 503 or 504 in front of an API that is down, and an HTML page where
// the API answers JSON or nothing, which is what a captive portal hands back for every address
// (`answeredByServer`).
//
// The state is `checking` until the first measure, then `online` or `offline`; nothing is assumed.
// `checking` counts as online, so the first request goes out and is itself the measure. While
// offline the probe repeats on a lengthening wait, from `firstWaitMs` doubling up to `longestWaitMs`,
// and at once on the browser's `online` event or the page coming back into view. While online nothing
// is polled: the site's own requests keep the flag true. The browser's `offline` event is believed at
// once; its `online` event only starts a probe.
//
//   const connectivity = createConnectivity({ probe: () => config.api + "/health" });
//   connectivity.start();
//   const api = createApi({ session, base, connectivity });
//   const online = useOnline(connectivity);          // @thekrystalship/auth-client/react
//   connectivity.onReconnect(() => refresh());

/** @typedef {"checking" | "online" | "offline"} Reach */

/**
 * @typedef {object} ConnectivitySnapshot
 * @property {Reach} state `checking` before the first measure, then what was last measured.
 * @property {number} since When the state last changed, in milliseconds since the epoch.
 */

/**
 * @typedef {object} ConnectivityOptions
 * @property {string | (() => string | null | undefined)} probe The address asked while offline: one that answers without a credential, such as the API's `/health`. A function is asked each time; an empty answer skips the probe.
 * @property {(response: Response) => boolean} [answered] Whether a response came from the site's own server; `answeredByServer` by default.
 * @property {number} [timeoutMs] How long a probe waits for an answer; 5000 by default.
 * @property {number} [firstWaitMs] The first wait between probes while offline; 2000 by default.
 * @property {number} [longestWaitMs] The longest wait between probes; 60000 by default.
 */

const GATEWAY = new Set([502, 503, 504]);

/**
 * Whether a response came from the server rather than from something standing in front of it: a
 * gateway answering for an API that is down, or a captive portal answering every address with its own
 * page.
 * @param {Response} response
 */
export function answeredByServer(response) {
  const type = (response.headers.get("content-type") || "").toLowerCase();
  if (type.startsWith("text/html")) return false;
  return !(GATEWAY.has(response.status) && !type.includes("json"));
}

/**
 * The site's connectivity.
 * @param {ConnectivityOptions} options
 */
export function createConnectivity(options) {
  const {
    answered = answeredByServer,
    timeoutMs = 5000,
    firstWaitMs = 2000,
    longestWaitMs = 60000,
  } = options;
  const probeUrl = () => (typeof options.probe === "function" ? options.probe() : options.probe) || "";
  const browserOffline = () => globalThis.navigator?.onLine === false;

  /** @type {ConnectivitySnapshot} */
  let snapshot = { state: browserOffline() ? "offline" : "checking", since: Date.now() };
  const listeners = new Set();
  const comebacks = new Set();
  let wait = firstWaitMs;
  /** @type {ReturnType<typeof setTimeout> | null} */
  let timer = null;
  /** @type {Promise<boolean> | null} */
  let probing = null;
  /** @type {(() => void) | null} */
  let stopListening = null;

  function emit() {
    for (const fn of listeners) fn();
  }

  function clearTimer() {
    if (timer) clearTimeout(timer);
    timer = null;
  }

  function schedule() {
    if (timer) return;
    timer = setTimeout(() => {
      timer = null;
      void probe();
    }, wait);
    wait = Math.min(longestWaitMs, wait * 2);
  }

  /** @param {boolean} online */
  function set(online) {
    const next = online ? "online" : "offline";
    if (snapshot.state === next) {
      if (!online) schedule();
      return;
    }
    const cameBack = online && snapshot.state === "offline";
    snapshot = { state: next, since: Date.now() };
    if (online) {
      clearTimer();
      wait = firstWaitMs;
    } else {
      schedule();
    }
    emit();
    if (cameBack) {
      for (const fn of comebacks) {
        try { fn(); } catch { /* one listener failing leaves the others to run */ }
      }
    }
  }

  /** Asks the probe address whether the server is there, and answers whether it was. One probe runs at a time. */
  function probe() {
    probing ??= (async () => {
      const url = probeUrl();
      if (!url) return snapshot.state !== "offline";
      try {
        const response = await fetch(url, {
          headers: { Accept: "application/json" },
          cache: "no-store",
          signal: AbortSignal.timeout(timeoutMs),
        });
        const ok = answered(response);
        set(ok);
        return ok;
      } catch {
        set(false);
        return false;
      }
    })().finally(() => { probing = null; });
    return probing;
  }

  const connectivity = {
    /** @returns {ConnectivitySnapshot} */
    getSnapshot: () => snapshot,
    /** @param {() => void} fn @returns {() => void} */
    subscribe(fn) {
      listeners.add(fn);
      return () => { listeners.delete(fn); };
    },
    /** Whether the site can reach its API, as last measured; `checking` counts as reachable. */
    isOnline: () => snapshot.state !== "offline",
    /** A request the server answered (`true`) or one that did not reach it (`false`). */
    report: (/** @type {boolean} */ reached) => set(reached),
    answered,
    probe,
    /**
     * Calls `fn` each time the site reaches its API again after losing it. Answers the way to stop.
     * @param {() => void} fn
     */
    onReconnect(fn) {
      comebacks.add(fn);
      return () => { comebacks.delete(fn); };
    },
    /** Starts listening for the network coming and going, and measures once. Answers the way to stop. */
    start() {
      if (stopListening) return stopListening;
      const w = globalThis.window;
      const onOffline = () => set(false);
      const onOnline = () => {
        clearTimer();
        wait = firstWaitMs;
        void probe();
      };
      const onVisible = () => {
        if (globalThis.document?.visibilityState === "visible" && snapshot.state === "offline") {
          clearTimer();
          void probe();
        }
      };
      w?.addEventListener("offline", onOffline);
      w?.addEventListener("online", onOnline);
      globalThis.document?.addEventListener("visibilitychange", onVisible);
      if (snapshot.state === "offline") schedule();
      else void probe();
      stopListening = () => {
        w?.removeEventListener("offline", onOffline);
        w?.removeEventListener("online", onOnline);
        globalThis.document?.removeEventListener("visibilitychange", onVisible);
        clearTimer();
        stopListening = null;
      };
      return stopListening;
    },
  };
  return connectivity;
}

/** @typedef {ReturnType<typeof createConnectivity>} Connectivity */
