// Gate — the one thing the admin pages draw about the session: its end. A session that is restoring
// or stale draws nothing of its own; the page keeps its last good view. Somebody who is signed out is
// sent to sign in, and nothing is drawn meanwhile.

import React from "react";

import { Button, CardScreen } from "@thekrystalship/krystal";
import { AdminApp } from "./AdminApp.jsx";
import { session, useSession } from "./auth.js";

const MARK = import.meta.env.BASE_URL + "assets/tks-mark.png";

// A session that ended because the account no longer administers says so; every other end is a
// sign-out.
function Ended({ reason }) {
  const tagline = reason === "denied" ? "You don’t have access to this." : "You have been signed out.";
  return (
    <CardScreen mark={MARK} wordmark="The Krystal Ship" tagline={tagline}>
      <Button variant="primary" size="lg" wide onClick={() => session.signIn()}>Sign in again</Button>
    </CardScreen>
  );
}

function Gate() {
  const snapshot = useSession(session);
  const signedOut = snapshot.status === "none";

  React.useEffect(() => {
    if (signedOut) session.signIn();
  }, [signedOut]);

  if (snapshot.status === "ended") return <Ended reason={snapshot.reason} />;
  if (signedOut) return null;
  return <AdminApp account={snapshot.account} />;
}

export { Gate };
