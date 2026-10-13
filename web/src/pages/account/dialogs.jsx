// The four dialogs of the account document. Each is a form: Enter submits, Cancel closes, and the
// provider's refusal stands in the dialog as a danger Notice.

import React from "react";
import { Button, Dialog, Field, Input, Notice, PasswordInput, ProviderButton, providerLabel } from "@thekrystalship/krystal-ui";
import { passwordOk } from "../../lib/credentialRules.js";
import { deleteJson, postJson } from "../api.js";
import { PasswordStrength } from "../PasswordStrength.jsx";

const submitting = (submit) => (e) => { e.preventDefault(); submit(); };

// Renaming a device signed in to this account: the name it is listed by here and carries on its tokens,
// which the device reads at its next refresh. An emptied field takes the name away.
function RenameDialog({ row, onClose, onDone }) {
  const [name, setName] = React.useState(row.name || "");
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState(null);

  const submit = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    const r = await postJson("/account/sessions/name", { sid: row.sid, name });
    setBusy(false);
    if (r.ok) onDone();
    else setError(r.error);
  };

  return (
    <Dialog title="Rename device" icon="tv" as="form" onSubmit={submitting(submit)}
      onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" type="submit" busy={busy}>{busy ? "Saving…" : "Save"}</Button>
        </>
      )}>
      <div data-dialog="rename">
        {error ? <Notice tone="danger">{error}</Notice> : null}
        <Field label="Name">
          <Input id="device-rename" wide type="text" value={name} maxLength={64} autoComplete="off" autoFocus
            disabled={busy} onChange={(e) => setName(e.target.value)} />
        </Field>
      </div>
    </Dialog>
  );
}

// Asking for a proof. The password where the account has one; otherwise a provider it already holds,
// which is a round trip rather than a field.
function ProveDialog({ me, onClose, onProved, onProvider }) {
  const [password, setPassword] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState(null);
  const providers = me.identities.map((i) => i.provider).filter((p) => me.providers.includes(p));

  const submit = async () => {
    if (!me.hasPassword || !password || busy) return;
    setBusy(true);
    setError(null);
    const r = await postJson("/account/reauth", { password });
    setBusy(false);
    if (r.ok) onProved();
    else setError(r.error);
  };

  return (
    <Dialog title="Confirm it’s you" icon="shield-check" as="form" onSubmit={submitting(submit)}
      onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          {me.hasPassword ? (
            <Button variant="primary" type="submit" busy={busy} disabled={!password}>{busy ? "Checking…" : "Confirm"}</Button>
          ) : null}
        </>
      )}>
      <div data-dialog="prove">
        {error ? <Notice tone="danger">{error}</Notice> : null}
        {me.hasPassword ? (
          <Field label="Password">
            <PasswordInput id="prove-password" wide value={password} autoComplete="current-password" autoFocus
              disabled={busy} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        ) : (
          <div className="account-body__stack">
            {providers.map((p) => (
              <ProviderButton key={p} provider={p} brand wide size="lg" onClick={() => onProvider(p)}>
                Continue with {providerLabel(p)}
              </ProviderButton>
            ))}
          </div>
        )}
      </div>
    </Dialog>
  );
}

function PasswordDialog({ hasPassword, onClose, onReauth, onDone }) {
  const [next, setNext] = React.useState("");
  const [again, setAgain] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState(null);
  const ready = passwordOk(next) && next === again;

  const submit = async () => {
    if (!ready || busy) return;
    setBusy(true);
    setError(null);
    const r = await postJson("/account/password", { password: next });
    setBusy(false);
    if (r.ok) onDone();
    else if (r.code === "reauth_required") onReauth();
    else setError(r.error);
  };

  return (
    <Dialog title={hasPassword ? "Change your password" : "Set a password"} icon="lock" as="form"
      onSubmit={submitting(submit)} onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" type="submit" busy={busy} disabled={!ready}>{busy ? "Saving…" : "Save password"}</Button>
        </>
      )}>
      <div data-dialog="password">
        {error ? <Notice tone="danger">{error}</Notice> : null}
        <Field label="New password">
          <PasswordInput id="pw-new" wide value={next} autoComplete="new-password" autoFocus disabled={busy}
            onChange={(e) => setNext(e.target.value)} />
          <PasswordStrength value={next} />
        </Field>
        <Field label="New password again" error={again && again !== next ? "Passwords don’t match" : undefined}>
          <PasswordInput id="pw-again" wide value={again} autoComplete="new-password" disabled={busy}
            invalid={!!again && again !== next} onChange={(e) => setAgain(e.target.value)} />
        </Field>
      </div>
    </Dialog>
  );
}

function DisconnectDialog({ identity, onClose, onReauth, onDone }) {
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState(null);

  const submit = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    const r = await deleteJson("/account/identities/" + encodeURIComponent(identity.id));
    setBusy(false);
    if (r.ok) onDone();
    else if (r.code === "reauth_required") onReauth();
    else setError(r.error);
  };

  return (
    <Dialog title={"Disconnect " + providerLabel(identity.provider) + "?"} icon="unlink"
      sub={identity.label || identity.handle} as="form" onSubmit={submitting(submit)}
      onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="danger" type="submit" busy={busy}>{busy ? "Disconnecting…" : "Disconnect"}</Button>
        </>
      )}>
      <div data-dialog="disconnect">
        {error ? <Notice tone="danger">{error}</Notice> : null}
      </div>
    </Dialog>
  );
}

export { DisconnectDialog, PasswordDialog, ProveDialog, RenameDialog };
