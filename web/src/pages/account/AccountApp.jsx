import React from "react";
import { EmptyState, Notice, Skeleton } from "@thekrystalship/krystal";
import { Frame } from "../../chrome/Frame.jsx";
import { parseTs } from "../../lib/formatting.js";
import { getJson, postJson } from "../api.js";
import { go, takeOutcome, useRoute } from "../route.js";
import { AppearancePage } from "./AppearancePage.jsx";
import { DevicesPage } from "./DevicesPage.jsx";
import { DisconnectDialog, PasswordDialog, ProveDialog, RenameDialog } from "./dialogs.jsx";
import { ProfilePage } from "./ProfilePage.jsx";
import { SignInMethodsPage } from "./SignInMethodsPage.jsx";

// AccountApp — somebody's own account, on the provider that holds it.
//
// The one place a person changes how they sign in: their password, the provider accounts attached to
// them, and where they are signed in. Every application links here rather than carrying a copy, so
// nothing an application holds can change a credential.
//
// A change to how somebody signs in needs a RECENT proof. The daemon says until when the last one is
// good (`freshUntil`); inside that window a change goes straight through, outside it the page asks for
// the password first — or, for an account with none, a round trip to a provider it already has. That
// round trip leaves the page, so what was being done is remembered in this tab and resumed on return.
//
// The same proof gates `auth:*` actions on every surface. A surface refused with `reauth_required`
// opens this page at `#confirm`, which asks for the proof straight away and changes nothing else.

const RESUME_KEY = "kgsm:account:resume";

function remember(action) {
  try { sessionStorage.setItem(RESUME_KEY, JSON.stringify(action)); } catch { /* private mode */ }
}
function takeRemembered() {
  try {
    const raw = sessionStorage.getItem(RESUME_KEY);
    sessionStorage.removeItem(RESUME_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch { return null; }
}

function isFresh(me) {
  return !!(me && me.freshUntil && parseTs(me.freshUntil) > Date.now());
}

function AccountApp() {
  const [me, setMe] = React.useState(null);
  const [loadError, setLoadError] = React.useState(null);
  const [outcome, setOutcome] = React.useState(() => takeOutcome());
  const page = useRoute();
  const [proving, setProving] = React.useState(null);   // the action waiting on a proof
  const [changingPassword, setChangingPassword] = React.useState(false);
  const [disconnecting, setDisconnecting] = React.useState(null);
  const [renaming, setRenaming] = React.useState(null);   // the device row being renamed
  const [busy, setBusy] = React.useState(null);
  const [error, setError] = React.useState(null);

  const reload = React.useCallback(async () => {
    const r = await getJson("/account/me");
    if (r.status === 401) { window.location.assign("/account/sign-in"); return null; }
    if (!r.ok) { setLoadError(r.error); return null; }
    setMe(r.body);
    return r.body;
  }, []);

  // The actions a proof guards, by name, so one begun before a provider round trip can be resumed
  // after it.
  const run = React.useCallback(async (action) => {
    setError(null);
    if (action.kind === "confirm") { setOutcome({ tone: "ok", text: "Confirmed." }); return; }
    if (action.kind === "password") { setChangingPassword(true); return; }
    if (action.kind === "unlink") { setDisconnecting(action.identity); return; }
    if (action.kind === "link") {
      setBusy("link:" + action.provider);
      const r = await postJson("/account/identities/" + encodeURIComponent(action.provider) + "/start");
      if (r.ok && r.body && r.body.url) { window.location.assign(r.body.url); return; }
      setBusy(null);
      if (r.code === "reauth_required") { setProving(action); return; }
      setError(r.error);
    }
  }, []);

  const guarded = React.useCallback((action) => {
    if (isFresh(me)) run(action);
    else setProving(action);
  }, [me, run]);

  React.useEffect(() => {
    reload().then((loaded) => {
      if (!loaded) return;
      const resumed = takeRemembered();
      if (outcome && outcome.tone === "proved") {
        setOutcome(null);
        if (resumed) run(resumed);
      } else if (outcome && outcome.tone === "confirm") {
        setOutcome(null);
        const confirm = { kind: "confirm" };
        if (isFresh(loaded)) run(confirm);
        else setProving(confirm);
      }
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once, on the load a round trip returns to
  }, []);

  const endSession = async (row) => {
    setBusy(row.sid);
    const r = await postJson("/account/sessions/revoke", { sid: row.sid });
    setBusy(null);
    if (!r.ok) { setError(r.error); return; }
    if (row.current) window.location.assign("/account/sign-in");
    else reload();
  };

  const endEverything = async () => {
    setBusy("all");
    const r = await postJson("/account/sessions/revoke", { all: true });
    if (!r.ok) { setBusy(null); setError(r.error); return; }
    window.location.assign("/account/sign-in");
  };

  const signOut = async () => {
    await postJson("/account/sign-out");
    window.location.assign("/account/sign-in");
  };

  const navigate = (place) => {
    setOutcome(null);
    setError(null);
    go(place.id);
  };

  let body;
  if (loadError) body = <EmptyState icon="wifi-off" tone="warn" title="Couldn’t reach the provider">{loadError}</EmptyState>;
  else if (!me) body = <Skeleton h={160} />;
  else {
    body = (
      <>
        {outcome && outcome.text ? (
          <div data-outcome={outcome.tone}>
            <Notice tone={outcome.tone === "ok" ? "ok" : "danger"}>{outcome.text}</Notice>
          </div>
        ) : null}
        {error ? <Notice tone="danger">{error}</Notice> : null}
        {page === "profile" ? <ProfilePage me={me} /> : null}
        {page === "sign-in" ? <SignInMethodsPage me={me} busy={busy} guarded={guarded} /> : null}
        {page === "devices" ? (
          <DevicesPage me={me} busy={busy} onRename={setRenaming} onEnd={endSession} onEndAll={endEverything} />
        ) : null}
        {page === "appearance" ? <AppearancePage /> : null}
      </>
    );
  }

  const user = me ? { name: me.username, display: me.displayName } : null;

  return (
    <Frame
      doc="account"
      active={page}
      showAdmin={!!(me && me.administers)}
      offered={["applications"]}
      user={user}
      onSignOut={signOut}
      onNavigate={navigate}>
      <div className="frame-body" aria-busy={!me && !loadError ? "true" : undefined}>{body}</div>

      {me && renaming ? (
        <RenameDialog row={renaming} onClose={() => setRenaming(null)} onDone={() => { setRenaming(null); reload(); }} />
      ) : null}

      {me && proving ? (
        <ProveDialog
          me={me}
          onClose={() => setProving(null)}
          onProvider={(provider) => {
            remember(proving);
            window.location.assign("/account/reauth/" + encodeURIComponent(provider));
          }}
          onProved={async () => {
            const action = proving;
            setProving(null);
            await reload();
            run(action);
          }} />
      ) : null}

      {me && changingPassword ? (
        <PasswordDialog
          hasPassword={me.hasPassword}
          onClose={() => setChangingPassword(false)}
          onReauth={() => { setChangingPassword(false); setProving({ kind: "password" }); }}
          onDone={() => {
            setChangingPassword(false);
            setOutcome({ tone: "ok", text: "Password saved." });
            reload();
          }} />
      ) : null}

      {me && disconnecting ? (
        <DisconnectDialog
          identity={disconnecting}
          onClose={() => setDisconnecting(null)}
          onReauth={() => { const identity = disconnecting; setDisconnecting(null); setProving({ kind: "unlink", identity }); }}
          onDone={() => { setDisconnecting(null); reload(); }} />
      ) : null}
    </Frame>
  );
}

export { AccountApp };
