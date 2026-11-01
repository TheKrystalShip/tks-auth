// A session with the package's interface and no provider behind it, so a site's test sets the snapshot it
// draws: `adopt` signs a person in from claims, `end` ends, `signOut` clears. `useSession` from
// `@thekrystalship/auth-client/react` reads it like any session.

const NONE = Object.freeze({ status: "none", reason: null, account: null, claims: {}, expiresAt: null });

/**
 * A session that implements the interface with no sign-in behind it.
 * @returns {import("./types.js").Session & { adopt: (tokens: { claims?: Record<string, any> }) => void }}
 */
export function createStandInSession() {
  /** @type {import("./types.js").Snapshot} */
  let snapshot = NONE;
  const listeners = new Set();
  const set = (next) => {
    snapshot = next;
    listeners.forEach((l) => l());
  };
  return /** @type {any} */ ({
    restore: async () => null,
    // Resolves false: there is no provider to leave for.
    signIn: async () => false,
    signOut: async () => set(NONE),
    // Rejects with `code` "ended" or "none" when there is no token to offer.
    fresh: async () => {
      throw Object.assign(new Error("No session."), { code: snapshot.status === "ended" ? "ended" : "none" });
    },
    token: () => null,
    authorizedFetch: (input, init) => fetch(input, init),
    adopt: (tokens) => {
      const claims = (tokens && tokens.claims) || {};
      set({
        status: "live", reason: null, claims, expiresAt: null,
        account: { id: claims.sub || null, name: claims.name || null, username: claims.preferred_username || null },
      });
    },
    end: (reason) => set({ ...NONE, status: "ended", reason }),
    updateAccount: (patch) => snapshot.account && set({ ...snapshot, account: { ...snapshot.account, ...patch } }),
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getSnapshot: () => snapshot,
    device: {
      // `poll` resolves "signed-in", "refused", "expired" or "cancelled".
      start: async () => ({ userCode: "", verificationUri: "", verificationUriComplete: "", expiresIn: 0, poll: async () => "cancelled", cancel() {} }),
    },
  });
}
