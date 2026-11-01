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

const session = createSession({
  resolve: async () => ({ issuer: window.location.origin, clientId: CLIENT_ID }),
  redirectPath: PATH,
  // The admin pages answer only a live administrator's session; signed out, the account page asks the
  // browser to sign in.
  postSignOutPath: "/account",
});

export { session };
