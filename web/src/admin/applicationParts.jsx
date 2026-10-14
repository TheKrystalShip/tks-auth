// applicationParts — what the Applications list and the application page share: reading a list typed
// into a field, a change run against the provider, the dialog that asks before one, and the fields of a
// client and the dialogs that take them.

import React from "react";

import { Button, Checkbox, Dialog, Field, Input, Textarea } from "@thekrystalship/krystal-ui";
import { api } from "./client.js";
import { RefusalNote } from "./kit.jsx";
import { refusalOf } from "./stores.js";

// A list typed one per line or separated by commas or spaces.
const listOf = (text) => String(text || "").split(/[\s,]+/).map((s) => s.trim()).filter(Boolean);

// A call against the provider as `{ ok, answer }` or `{ ok: false, refusal }`.
async function attempt(call) {
  try {
    return { ok: true, answer: await call() };
  } catch (e) {
    return { ok: false, refusal: refusalOf(e) };
  }
}

// Asking before a change that cannot be taken back. `onConfirm` resolves to `{ ok, refusal }`; the
// dialog closes by the caller's hand on success and shows the refusal otherwise.
function ConfirmDialog({ kind, title, icon, text, confirm, onConfirm, onClose }) {
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);

  const run = async () => {
    setBusy(true);
    setRefusal(null);
    const r = await onConfirm();
    if (r.ok) return;
    setBusy(false);
    setRefusal(r.refusal);
  };

  return (
    <Dialog title={title} icon={icon} onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="danger" icon={icon} busy={busy} onClick={run}>{confirm}</Button>
        </>
      )}>
      <div data-dialog={kind} className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <p className="admin-dialog-text">{text}</p>
      </div>
    </Dialog>
  );
}

// One client's registration, held as the fields hold it. An empty id is made by the provider.
function useClientFields() {
  const [client, setClient] = React.useState({ clientId: "", redirects: "", postLogout: "", confidential: false, device: false });
  const text = (k) => (e) => setClient((c) => ({ ...c, [k]: e.target.value }));
  const flag = (k) => (on) => setClient((c) => ({ ...c, [k]: on }));
  const body = {
    clientId: client.clientId.trim() || null,
    redirectUris: listOf(client.redirects),
    postLogoutRedirectUris: listOf(client.postLogout),
    confidential: client.confidential,
    device: client.device,
  };
  return { client, text, flag, body };
}

function ClientFields({ prefix, client, text, flag, busy }) {
  return (
    <>
      <Field label="Client id" aside="optional">
        <Input id={prefix + "-client"} wide mono spellCheck="false" autoCapitalize="off" value={client.clientId}
          disabled={busy} onChange={text("clientId")} />
      </Field>
      <Field label="Redirect URIs">
        <Textarea id={prefix + "-redirects"} mono spellCheck="false" rows={3} value={client.redirects}
          disabled={busy} onChange={text("redirects")} />
      </Field>
      <Field label="Post-sign-out URIs">
        <Textarea id={prefix + "-post-logout"} mono spellCheck="false" rows={2} value={client.postLogout}
          disabled={busy} onChange={text("postLogout")} />
      </Field>
      <Checkbox checked={client.confidential} disabled={busy} onChange={flag("confidential")}>Confidential</Checkbox>
      <Checkbox checked={client.device} disabled={busy} onChange={flag("device")}>Device sign-in</Checkbox>
    </>
  );
}

// Adding a client to an application.
function ClientDialog({ applicationId, onDone, onClose }) {
  const { client, text, flag, body } = useClientFields();
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);

  const send = async (e) => {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setRefusal(null);
    const r = await attempt(() => api.applications.addClient(applicationId, body));
    if (r.ok) { onDone(r.answer); return; }
    setBusy(false);
    setRefusal(r.refusal);
  };

  return (
    <Dialog title="Add a client" icon="plus" as="form" onSubmit={send} onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" type="submit" busy={busy}>Add client</Button>
        </>
      )}>
      <div data-dialog="client-add" className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <ClientFields prefix="new" client={client} text={text} flag={flag} busy={busy} />
      </div>
    </Dialog>
  );
}

// Registering an application with its first client.
function RegisterDialog({ onDone, onClose }) {
  const [app, setApp] = React.useState({ id: "", name: "", audience: "", manifestUrl: "", lifetime: "", discord: "", actForDiscord: false });
  const { client, text: clientText, flag: clientFlag, body: clientBody } = useClientFields();
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);
  const text = (k) => (e) => setApp((a) => ({ ...a, [k]: e.target.value }));
  const id = app.id.trim();

  const send = async (e) => {
    e.preventDefault();
    if (!id || busy) return;
    setBusy(true);
    setRefusal(null);
    const hasClient = clientBody.clientId || clientBody.redirectUris.length || clientBody.confidential || clientBody.device;
    const r = await attempt(() => api.applications.add({
      id,
      name: app.name.trim() || id,
      audience: app.audience.trim() || null,
      manifestUrl: app.manifestUrl.trim() || null,
      accessLifetimeMinutes: app.lifetime ? Number(app.lifetime) : null,
      discordApplications: listOf(app.discord),
      actForDiscord: app.actForDiscord,
      clients: hasClient ? [clientBody] : [],
    }));
    if (r.ok) { onDone(id, r.answer); return; }
    setBusy(false);
    setRefusal(r.refusal);
  };

  return (
    <Dialog title="Register an application" icon="app-window" as="form" onSubmit={send} onClose={onClose}
      canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" type="submit" busy={busy} disabled={!id}>Register</Button>
        </>
      )}>
      <div data-dialog="application-register" className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <Field label="Id">
          <Input id="app-id" wide mono autoFocus spellCheck="false" autoCapitalize="off" value={app.id} disabled={busy}
            onChange={text("id")} />
        </Field>
        <Field label="Name">
          <Input id="app-name" wide value={app.name} disabled={busy} onChange={text("name")} />
        </Field>
        <Field label="Audience" aside="optional">
          <Input id="app-audience" wide mono spellCheck="false" autoCapitalize="off" placeholder={id} value={app.audience}
            disabled={busy} onChange={text("audience")} />
        </Field>
        <Field label="Manifest" aside="optional">
          <Input id="app-manifest" wide mono spellCheck="false" autoCapitalize="off" value={app.manifestUrl}
            disabled={busy} onChange={text("manifestUrl")} />
        </Field>
        <Field label="Access lifetime (minutes)" aside="optional">
          <Input id="app-lifetime" wide type="number" placeholder="5" value={app.lifetime} disabled={busy}
            onChange={text("lifetime")} />
        </Field>
        <Field label="Discord applications" aside="optional">
          <Input id="app-discord" wide mono spellCheck="false" autoCapitalize="off" value={app.discord}
            disabled={busy} onChange={text("discord")} />
        </Field>
        <Checkbox checked={app.actForDiscord} disabled={busy}
          onChange={(on) => setApp((a) => ({ ...a, actForDiscord: on }))}>Acts for linked Discord users</Checkbox>
        <h3 className="admin-form__title">First client</h3>
        <ClientFields prefix="register" client={client} text={clientText} flag={clientFlag} busy={busy} />
      </div>
    </Dialog>
  );
}

export { ClientDialog, ConfirmDialog, RegisterDialog, attempt, listOf };
