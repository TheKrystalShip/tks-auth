// ApplicationPage — one application: its settings, its clients and their secrets, and removing it.
//
// Reached at `#/applications/<id>` from the Applications list. The settings are fields on the page,
// saved together with only what changed; a client is added in a dialog, and removing a client or the
// application is asked for first. A secret the provider has just made (registering, a new secret) stands
// in a notice with a way to copy it until it is dismissed or the page is left, and is held nowhere else.
// KGSM and tks-auth are built in and read-only.

import React from "react";

import {
  Breadcrumbs, Button, CopyField, Field, IconButton, Input, List, ListRow, Notice, PageHeader, Pill,
  SettingsRow, SettingsSection, Toggle, toast, useStore,
} from "@thekrystalship/krystal-ui";
import { identityText, manifestText } from "./ApplicationsPage.jsx";
import { ClientDialog, ConfirmDialog, attempt, listOf } from "./applicationParts.jsx";
import { issuedStore, keepSecrets, refreshApplications, useApplications } from "./applicationsStore.js";
import { APPLICATIONS, api } from "./client.js";
import { Brief, Loading, RefusalNote } from "./kit.jsx";
import { go } from "./route.js";
import { callRefusal } from "./stores.js";

const backToList = <Button variant="outline" icon="arrow-left" onClick={() => go("applications")}>Back to applications</Button>;

function ApplicationPage({ id }) {
  const { allowed, status, list, error } = useApplications();
  const issued = useStore(issuedStore);
  const [refusal, setRefusal] = React.useState(null);
  const [busy, setBusy] = React.useState(false);
  const [adding, setAdding] = React.useState(false);
  const [removing, setRemoving] = React.useState(false);
  const [removingClient, setRemovingClient] = React.useState(null);
  const [rotating, setRotating] = React.useState(null);

  // A secret is shown on the page it was made for and nowhere after it.
  React.useEffect(() => () => issuedStore.setState(null), []);

  if (!allowed) return <Brief icon="lock" title="You don’t have access to this" />;
  if (status === "error" && !list) return <Brief icon="wifi-off" tone="warn" title="Couldn’t read the applications" sub={error && error.userMessage} />;
  if (!list) return <Loading />;
  const app = list.find((a) => a.id === id);
  if (!app) return <Brief icon="app-window" title="No such application" actions={backToList} />;

  const builtin = app.source === "builtin";
  const path = APPLICATIONS + "/" + encodeURIComponent(app.id);
  const clientsPath = path + "/clients";
  const canChange = !builtin && !callRefusal("PATCH", path);
  const canRemove = !builtin && !callRefusal("DELETE", path);
  const canAdd = !builtin && !callRefusal("POST", clientsPath);
  const secrets = issued && issued.applicationId === app.id ? issued.secrets : [];

  const answered = (answer) => {
    keepSecrets(app.id, answer);
    return refreshApplications();
  };

  const save = async (change) => {
    setBusy(true);
    setRefusal(null);
    const r = await attempt(() => api.applications.set(app.id, change));
    if (r.ok) { await answered(r.answer); toast.success("Saved"); } else setRefusal(r.refusal);
    setBusy(false);
  };

  const confirmed = (call) => async () => {
    const r = await attempt(call);
    if (r.ok) await answered(r.answer);
    return r;
  };

  const manifest = manifestText(app);

  return (
    <>
      <Breadcrumbs items={[{ label: "Applications", href: "#/applications" }, { label: app.name }]} onNavigate={() => go("applications")} />
      <PageHeader title={app.name}
        sub={identityText(app, manifest)}
        actions={canRemove ? (
          <Button variant="outline" tone="danger" icon="trash-2" disabled={busy} onClick={() => setRemoving(true)}>Remove</Button>
        ) : builtin ? <Pill tone="muted" icon="lock">Built in</Pill> : null} />
      <RefusalNote refusal={refusal} />

      {secrets.length > 0 && (
        <Notice tone="warn" icon="key-round" className="admin-secrets">
          <div className="admin-secrets__title">Client secret, shown once. Copy it now; it cannot be read again.</div>
          {secrets.map((s) => (
            <div key={s.clientId} className="admin-secret" data-secret-for={s.clientId}>
              <code className="admin-secret__client">{s.clientId}</code>
              <CopyField value={s.secret} aria-label={"Secret for " + s.clientId} />
            </div>
          ))}
          <Button variant="outline" size="sm" icon="check" onClick={() => issuedStore.setState(null)}>Done</Button>
        </Notice>
      )}

      {builtin
        ? <BuiltInSettings app={app} manifest={manifest} />
        : <Settings app={app} editable={canChange} busy={busy} onSave={save} />}

      <SettingsSection icon="key-round" title="Clients"
        action={canAdd ? <Button variant="outline" size="sm" icon="plus" disabled={busy} onClick={() => setAdding(true)}>Add a client</Button> : null}>
        {app.clients.length === 0 && <div className="admin-quiet">No clients.</div>}
        {app.clients.length > 0 && (
          <List label="Clients" className="admin-flat-list">
            {app.clients.map((c) => {
              const one = clientsPath + "/" + encodeURIComponent(c.clientId);
              const canRotate = !builtin && c.confidential && !callRefusal("POST", one + "/secret");
              const canRemoveClient = !builtin && !callRefusal("DELETE", one);
              return (
                <ListRow key={c.clientId} data-client={c.clientId}
                  title={<code>{c.clientId}</code>}
                  sub={c.redirectUris.join(" · ") || "No redirect addresses"}
                  meta={<>
                    <Pill size="sm" tone={c.confidential ? "info" : "neutral"}>{c.confidential ? "confidential" : "public"}</Pill>
                    {c.device && <Pill size="sm">device</Pill>}
                  </>}
                  aside={c.memberId || c.source}
                  actions={canRotate || canRemoveClient ? (
                    <>
                      {canRotate && <Button variant="outline" size="sm" icon="refresh-cw" disabled={busy}
                        onClick={() => setRotating(c.clientId)}>New secret</Button>}
                      {canRemoveClient && <IconButton variant="ghost" size="sm" icon="trash-2" tone="danger"
                        label={"Remove " + c.clientId} disabled={busy} onClick={() => setRemovingClient(c.clientId)} />}
                    </>
                  ) : null} />
              );
            })}
          </List>
        )}
      </SettingsSection>

      {adding && (
        <ClientDialog applicationId={app.id} onClose={() => setAdding(false)}
          onDone={async (answer) => { await answered(answer); setAdding(false); }} />
      )}
      {rotating && (
        <ConfirmDialog kind="client-rotate" title={`New secret for ${rotating}?`} icon="refresh-cw" confirm="Make a new secret"
          text="The current secret stops working at once, and the new one is shown once."
          onClose={() => setRotating(null)}
          onConfirm={confirmed(async () => { const a = await api.applications.rotateSecret(app.id, rotating); setRotating(null); return a; })} />
      )}
      {removingClient && (
        <ConfirmDialog kind="client-remove" title={`Remove ${removingClient}?`} icon="trash-2" confirm="Remove client"
          text="Sign-ins through this client stop working. This cannot be undone."
          onClose={() => setRemovingClient(null)}
          onConfirm={confirmed(async () => { const a = await api.applications.removeClient(app.id, removingClient); setRemovingClient(null); return a; })} />
      )}
      {removing && (
        <ConfirmDialog kind="application-remove" title={`Remove ${app.name}?`} icon="trash-2" confirm="Remove application"
          text="This removes the application, its clients and its actions from the catalog. It cannot be undone."
          onClose={() => setRemoving(false)}
          onConfirm={async () => {
            const r = await attempt(() => api.applications.remove(app.id));
            if (r.ok) { await refreshApplications(); go("applications"); }
            return r;
          }} />
      )}
    </>
  );
}

// An application's own fields; only what changed is sent.
function Settings({ app, editable, busy, onSave }) {
  const initial = React.useMemo(() => ({
    name: app.name,
    audience: app.audience,
    manifestUrl: app.manifestUrl || "",
    lifetime: String(app.accessLifetimeMinutes),
    discord: (app.discordApplications || []).join(", "),
    actForDiscord: !!app.actForDiscord,
  }), [app]);
  const [form, setForm] = React.useState(initial);
  React.useEffect(() => { setForm(initial); }, [initial]);
  const text = (k) => (e) => setForm((f) => ({ ...f, [k]: e.target.value }));

  const change = {};
  if (form.name.trim() !== initial.name) change.name = form.name.trim();
  if (form.audience.trim() !== initial.audience) change.audience = form.audience.trim();
  if (form.manifestUrl.trim() !== initial.manifestUrl) change.manifestUrl = form.manifestUrl.trim();
  if (form.lifetime !== initial.lifetime) change.accessLifetimeMinutes = Number(form.lifetime);
  if (listOf(form.discord).join(",") !== listOf(initial.discord).join(",")) change.discordApplications = listOf(form.discord);
  if (form.actForDiscord !== initial.actForDiscord) change.actForDiscord = form.actForDiscord;
  const dirty = Object.keys(change).length > 0;
  const off = !editable || busy;

  return (
    <SettingsSection icon="settings" title="Settings"
      action={editable && dirty ? (
        <Button variant="primary" size="sm" busy={busy} disabled={busy} onClick={() => onSave(change)}>Save</Button>
      ) : null}>
      <form className="admin-form admin-section-fields" onSubmit={(e) => { e.preventDefault(); if (dirty && !off) onSave(change); }}>
        <Field label="Name">
          <Input id={"name-" + app.id} value={form.name} disabled={off} onChange={text("name")} />
        </Field>
        <Field label="Audience">
          <Input id={"audience-" + app.id} mono spellCheck="false" autoCapitalize="off" value={form.audience}
            disabled={off} onChange={text("audience")} />
        </Field>
        <Field label="Manifest" hint={manifestText(app) || undefined}>
          <Input id={"manifest-" + app.id} wide mono spellCheck="false" autoCapitalize="off" value={form.manifestUrl}
            disabled={off} onChange={text("manifestUrl")} />
        </Field>
        <Field label="Access lifetime (minutes)">
          <Input id={"lifetime-" + app.id} className="admin-input--short" type="number" value={form.lifetime} disabled={off} onChange={text("lifetime")} />
        </Field>
        <Field label="Discord applications" hint="Whose tokens this application’s clients may present.">
          <Input id={"discord-" + app.id} wide mono spellCheck="false" autoCapitalize="off" value={form.discord}
            disabled={off} onChange={text("discord")} />
        </Field>
      </form>
      <SettingsRow icon="link" title="Acts for linked Discord users">
        <Toggle on={form.actForDiscord} disabled={off} label="Acts for linked Discord users"
          onChange={(on) => setForm((f) => ({ ...f, actForDiscord: on }))} />
      </SettingsRow>
    </SettingsSection>
  );
}

// A built-in application's fields, read and never changed here.
function BuiltInSettings({ app, manifest }) {
  return (
    <SettingsSection icon="settings" title="Settings">
      <SettingsRow icon="id-card" title="Audience" sub={app.audience} />
      <SettingsRow icon="timer" title="Access lifetime" sub={app.accessLifetimeMinutes + (app.accessLifetimeMinutes === 1 ? " minute" : " minutes")} />
      {manifest && <SettingsRow icon="file-json" title="Manifest" sub={manifest} />}
    </SettingsSection>
  );
}

export { ApplicationPage };
