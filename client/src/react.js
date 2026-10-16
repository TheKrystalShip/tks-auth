import { useSyncExternalStore } from "react";

/**
 * The session's snapshot, re-rendering when it changes.
 * @param {import("./types.js").Session} session
 * @returns {import("./types.js").Snapshot}
 */
export function useSession(session) {
  return useSyncExternalStore(session.subscribe, session.getSnapshot, session.getSnapshot);
}
