// AccountPage — one account: who it is, where it stands and how it signs in, where it is signed in,
// the roles it holds, and ending it.
//
// Reached at `#/accounts/<id>` from the Accounts list. The provider offers no read of one account, so
// the page finds it in the list it reads, and an id the list does not hold is "No such account".
//
// Signing somebody out is deliberately NOT the same act as disabling them: the sessions end and the
// account is untouched, so they can sign straight back in. The confirmation says so, because somebody
// reaching for it during an incident is usually reaching for the other one. A session's recency is its
// last token rotation, not the person's last request: the provider sees a session only when it is
// refreshed.
//
// Every control is gated on the request it would make and every refusal is the provider's sentence.

import React from "react";

import {
  Breadcrumbs, Button, Dialog, Field, PageHeader, PasswordInput, providerLabel, SettingsRow,
  SettingsSection, useStore,
} from "@thekrystalship/krystal-ui";
import { deviceIcon, deviceKind, deviceName } from "../lib/devices.js";
import { when } from "../lib/formatting.js";
import { StatusPill, handleOf, nameOf, providersOf, useAccounts } from "./accountParts.jsx";
import { Assignments } from "./Assignments.jsx";
import { ConfirmRevokeDialog } from "./ConfirmRevokeDialog.jsx";
import { api } from "./client.js";
import { Brief, Loading, RefusalNote, authorityGate } from "./kit.jsx";
import { go } from "./route.js";
import { accessStore, authorityStore, refusalOf, userRefusal } from "./stores.js";

function AccountPage({ id }) {
  useStore(accessStore, (s) => s.report);
  const authority = useStore(authorityStore);
  const { rows, refusal, setRefusal, reload } = useAccounts();
  const [acting, setActing] = React.useState(false);
  const [password, setPassword] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);

  const user = (rows || []).find((u) => u.id === id);

  if (rows === null) return <Loading />;
  if (!user) {
    return (
      <>
        <RefusalNote refusal={refusal} />
        <Brief icon="user-x" title="No such account"
          actions={<Button variant="outline" icon="arrow-left" onClick={() => go("accounts")}>Back to accounts</Button>} />
      </>
    );
  }

  const name = nameOf(user);
  const canApprove = !userRefusal("PATCH", "/_", { status: "active" });
  const canDisable = !userRefusal("PATCH", "/_", { status: "disabled" });
  const canPassword = !userRefusal("POST", "/_/password");
  const canDelete = !userRefusal("DELETE", "/_");
  const canSessions = !userRefusal("GET", "/_/sessions");

  const setStatus = (status) => {
    setActing(true);
    setRefusal(null);
    api.users.update(user.id, { status }).then(
      () => reload().then(() => setActing(false)),
      (e) => { setRefusal(refusalOf(e)); setActing(false); });
  };

  const identities = user.identities || [];
  const providers = providersOf(user);

  return (
    <>
      <Breadcrumbs items={[{ label: "Accounts", href: "#/accounts" }, { label: name }]}
        onNavigate={() => go("accounts")} />
      <PageHeader title={name} sub={user.username}
        actions={(
          <>
            {user.status === "pending" && canApprove && (
              <Button variant="primary" icon="user-check" busy={acting} onClick={() => setStatus("active")}>Approve</Button>
            )}
            {user.status === "disabled" && canDisable && (
              <Button variant="primary" icon="user-check" busy={acting} onClick={() => setStatus("active")}>Enable</Button>
            )}
            {user.status !== "disabled" && canDisable && (
              <Button variant="outline" icon="user-x" busy={acting} onClick={() => setStatus("disabled")}>Disable</Button>
            )}
          </>
        )} />
      <RefusalNote refusal={refusal} />

      <SettingsSection icon="user" title="Profile">
        <SettingsRow icon="at-sign" title="Username" sub={user.username} />
        <SettingsRow icon="contact" title="Display name" sub={user.displayName || user.username} />
        <SettingsRow icon="circle-dot" title="Status">
          <StatusPill status={user.status} />
          {user.status === "active" && canApprove && (
            <Button variant="outline" size="sm" disabled={acting} onClick={() => setStatus("pending")}>Return to waiting</Button>
          )}
        </SettingsRow>
      </SettingsSection>

      <SettingsSection icon="key-round" title="Sign-in methods">
        <SettingsRow icon="lock" title="Password" sub={user.hasPassword ? "Set" : "None"}>
          {canPassword && (
            <Button variant="outline" size="sm" onClick={() => setPassword(true)}>
              {user.hasPassword ? "Reset password" : "Set a password"}
            </Button>
          )}
        </SettingsRow>
        {identities.map((i) => (
          <SettingsRow key={handleOf(i)} icon="link" title={providerLabel(typeof i === "string" ? i.split(":")[0] : i.provider)}
            sub={handleOf(i)} />
        ))}
        {!user.hasPassword && providers.length === 0 && (
          <SettingsRow icon="circle-alert" tone="warn" title="No way to sign in" sub="This account holds no password and no provider." />
        )}
      </SettingsSection>

      {canSessions && <SessionsSection user={user} name={name} />}

      <SettingsSection icon="shield" title="Roles held">
        {authority.view ? <Assignments view={authority.view} accountId={user.id} /> : authorityGate(authority)}
      </SettingsSection>

      {canDelete && (
        <SettingsSection icon="trash-2" title="Delete">
          <SettingsRow icon="trash-2" tone="danger" title="Delete this account" sub="Removes the account from this provider.">
            <Button variant="danger" size="sm" onClick={() => setDeleting(true)}>Delete</Button>
          </SettingsRow>
        </SettingsSection>
      )}

      {password && (
        <PasswordDialog user={user} name={name} onClose={() => setPassword(false)}
          onDone={() => { setPassword(false); reload(); }} />
      )}
      {deleting && (
        <DeleteDialog user={user} name={name} onClose={() => setDeleting(false)} onDone={() => go("accounts")} />
      )}
    </>
  );
}

// Where this account is signed in, and ending any of it.
function SessionsSection({ user, name }) {
  const [rows, setRows] = React.useState(null);   // null = not loaded
  const [error, setError] = React.useState(null);
  const [busy, setBusy] = React.useState(null);   // a sid, "all", or null
  const [confirm, setConfirm] = React.useState(null);

  const reload = React.useCallback(() => {
    setError(null);
    return api.sessions.list(user.id).then(
      (s) => setRows(s),
      (e) => { setRows([]); setError(refusalOf(e)); });
  }, [user.id]);

  React.useEffect(() => { reload(); }, [reload]);

  const run = () => {
    if (!confirm) return;
    const all = confirm.mode === "other-all";
    setBusy(all ? "all" : confirm.sid);
    const call = all ? api.sessions.revokeAll(user.id) : api.sessions.revokeOne(user.id, confirm.sid);
    call.then(
      () => { setConfirm(null); setBusy(null); reload(); },
      (e) => { setConfirm(null); setBusy(null); setError(refusalOf(e)); });
  };

  return (
    <SettingsSection icon="monitor-smartphone" title="Sessions"
      action={rows && rows.length > 0 ? (
        <Button variant="outline" size="sm" tone="danger" disabled={busy != null} onClick={() => setConfirm({ mode: "other-all" })}>
          {busy === "all" ? "Signing out…" : "Sign out everywhere"}
        </Button>
      ) : null}>
      <RefusalNote refusal={error} compact />
      {rows === null && !error && <Loading />}
      {rows !== null && rows.length === 0 && !error && <div className="admin-quiet">No active sessions.</div>}
      {(rows || []).map((s) => (
        <SettingsRow key={s.sid} icon={deviceIcon(s)} title={s.name || deviceName(s.userAgent)}
          sub={deviceKind(s) + " · signed in " + when(s.created) + (s.lastSeen ? " · seen " + when(s.lastSeen) : "")}>
          <Button variant="outline" size="sm" data-session={s.sid} title={s.userAgent || undefined} busy={busy === s.sid} disabled={busy != null}
            onClick={() => setConfirm({ mode: "other-one", sid: s.sid })}>
            {busy === s.sid ? "Ending…" : "End"}
          </Button>
        </SettingsRow>
      ))}
      {confirm && (
        <ConfirmRevokeDialog mode={confirm.mode} targetName={name} busy={busy != null}
          onClose={() => setConfirm(null)} onConfirm={run} />
      )}
    </SettingsSection>
  );
}

// A new password for somebody who has lost theirs. It knows no current one.
function PasswordDialog({ user, name, onClose, onDone }) {
  const [value, setValue] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);

  const submit = async (e) => {
    e.preventDefault();
    if (!value || busy) return;
    setBusy(true);
    setRefusal(null);
    try {
      await api.users.setPassword(user.id, value);
      onDone();
    } catch (err) {
      setBusy(false);
      setRefusal(refusalOf(err));
    }
  };

  return (
    <Dialog title={user.hasPassword ? "Reset password" : "Set a password"} icon="lock" sub={name} as="form" onSubmit={submit}
      onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" type="submit" busy={busy} disabled={!value}>{busy ? "Saving…" : "Save"}</Button>
        </>
      )}>
      <div data-dialog="set-password" className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <Field label="New password">
          <PasswordInput id="account-password" wide value={value} autoComplete="new-password" autoFocus
            disabled={busy} onChange={(e) => setValue(e.target.value)} />
        </Field>
      </div>
    </Dialog>
  );
}

function DeleteDialog({ user, name, onClose, onDone }) {
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);

  const remove = async () => {
    setBusy(true);
    setRefusal(null);
    try {
      await api.users.remove(user.id);
      onDone();
    } catch (err) {
      setBusy(false);
      setRefusal(refusalOf(err));
    }
  };

  return (
    <Dialog title={`Delete ${name}?`} icon="trash-2" onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="danger" icon="trash-2" busy={busy} onClick={remove}>{busy ? "Deleting…" : "Delete account"}</Button>
        </>
      )}>
      <div data-dialog="delete-account" className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <p className="admin-dialog-text">This removes {name} ({user.username}) from this provider. It cannot be undone.</p>
      </div>
    </Dialog>
  );
}

export { AccountPage };
