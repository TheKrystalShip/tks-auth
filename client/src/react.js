import { useSyncExternalStore } from "react";

/**
 * The session's snapshot, re-rendering when it changes.
 * @param {import("./types.js").Session} session
 * @returns {import("./types.js").Snapshot}
 */
export function useSession(session) {
  return useSyncExternalStore(session.subscribe, session.getSnapshot, session.getSnapshot);
}

/**
 * Whether the site can reach its API, re-rendering when that changes: what a page draws its offline
 * state by. `checking`, before the first measure, counts as reachable.
 * @param {import("./connectivity.js").Connectivity} connectivity
 * @returns {boolean}
 */
export function useOnline(connectivity) {
  const state = useSyncExternalStore(connectivity.subscribe, () => connectivity.getSnapshot().state, () => connectivity.getSnapshot().state);
  return state !== "offline";
}
