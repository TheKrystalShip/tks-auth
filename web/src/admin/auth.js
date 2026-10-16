// auth.js — the admin pages' session, in one module: the rest of the pages import `session` and
// `useSession` from here and nothing else from the sign-in code.
//
// The admin pages are a public client of the provider that served them: the built-in client `tks-auth`,
// its callback `/admin/` on the issuer's own origin. The provider is audience-restricted to
// administrators, so a refresh it refuses because the account no longer administers ends the session
// as `denied`.

import { createSession } from "@thekrystalship/auth-client";

export { useSession } from "@thekrystalship/auth-client/react";

const CLIENT_ID = "tks-auth";
const PATH = "/admin/";

// A session a browser holds under `tks-auth.admin.user:<issuer>:<client>` is carried, unchanged, to the
// package's `tks-auth-client:<client>:user:<issuer>:<client>`, so an administrator signed in there stays
// signed in. A session already under the package's key wins, and the other key is removed either way.
function adoptStoredUser(store, issuer) {
  try {
    const oldKey = `tks-auth.admin.user:${issuer}:${CLIENT_ID}`;
    const newKey = `tks-auth-client:${CLIENT_ID}:user:${issuer}:${CLIENT_ID}`;
    const stored = store.getItem(oldKey);
    if (stored === null) return;
    if (store.getItem(newKey) === null) store.setItem(newKey, stored);
    store.removeItem(oldKey);
  } catch { /* storage is unavailable: there is nothing to carry */ }
}

const session = createSession({
  resolve: async () => {
    const issuer = window.location.origin;
    adoptStoredUser(window.localStorage, issuer);
    return { issuer, clientId: CLIENT_ID };
  },
  redirectPath: PATH,
  postSignOutPath: PATH,
});

export { adoptStoredUser, session };
