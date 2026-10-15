// ApplicationPage — one application: its settings, its clients and their secrets, and removing it.
//
// Reached at `#/applications/<id>` from the Applications list. The settings are fields on the page,
// saved together with only what changed; a client is added in a dialog, and removing a client or the
// application is asked for first. A secret the provider has just made (registering, a new secret) stands
// in a notice with a way to copy it until it is dismissed or the page is left, and is held nowhere else.
// KGSM and tks-auth are built in and read-only.

import React from "react";

import {
  Button, ConfirmDialog, CopyField, DetailPage, Form, FormField, IconButton, List, ListRow, Notice, Pill,
  SettingsRow, SettingsSection, Toggle, toast, useForm, useStore,
} from "@thekrystalship/krystal";
import { identityText, manifestText } from "./ApplicationsPage.jsx";
import { ClientDialog, attempt, listOf } from "./applicationParts.jsx";
import { issuedStore, keepSecrets, refreshApplications, useApplications } from "./applicationsStore.js";
import { APPLICATIONS, api } from "./client.js";
import { Brief, RefusalNote, confirmed } from "./kit.jsx";
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
  const app = list && list.find((a) => a.id === id);
  if (list && !app) return <Brief icon="app-window" title="No such application" actions={backToList} />;

  if (!app) return <DetailPage loading />;

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

  const confirming = (call) => async () => {
    const r = await attempt(call);
    if (r.ok) await answered(r.answer);
    return confirmed(r);
  };

  const manifest = manifestText(app);

  return (
    <DetailPage
      crumbs={[{ label: "Applications", href: "#/applications" }, { label: app.name }]} onNavigate={() => go("applications")}
      title={app.name}
      sub={identityText(app, manifest)}
      actions={canRemove ? (
        <Button variant="outline" tone="danger" icon="trash-2" disabled={busy} onClick={() => setRemoving(true)}>Remove</Button>
      ) : builtin ? <Pill tone="muted" icon="lock">Built in</Pill> : null}>
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
        <ConfirmDialog data-dialog="client-rotate" title={`New secret for ${rotating}?`} icon="refresh-cw" tone="danger"
          confirm="Make a new secret" onClose={() => setRotating(null)}
          onConfirm={confirming(() => api.applications.rotateSecret(app.id, rotating))}>
          The current secret stops working at once, and the new one is shown once.
        </ConfirmDialog>
      )}
      {removingClient && (
        <ConfirmDialog data-dialog="client-remove" title={`Remove ${removingClient}?`} icon="trash-2" tone="danger"
          confirm="Remove client" onClose={() => setRemovingClient(null)}
          onConfirm={confirming(() => api.applications.removeClient(app.id, removingClient))}>
          Sign-ins through this client stop working. This cannot be undone.
        </ConfirmDialog>
      )}
      {removing && (
        <ConfirmDialog data-dialog="application-remove" title={`Remove ${app.name}?`} icon="trash-2" tone="danger"
          confirm="Remove application" onClose={() => setRemoving(false)}
          onConfirm={async () => {
            const r = await attempt(() => api.applications.remove(app.id));
            if (r.ok) { await refreshApplications(); go("applications"); }
            return confirmed(r);
          }}>
          This removes the application, its clients and its actions from the catalog. It cannot be undone.
        </ConfirmDialog>
      )}
    </DetailPage>
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
  const form = useForm({
    values: initial,
    onSubmit: async (v) => {
      const change = {};
      if (v.name.trim() !== initial.name) change.name = v.name.trim();
      if (v.audience.trim() !== initial.audience) change.audience = v.audience.trim();
      if (v.manifestUrl.trim() !== initial.manifestUrl) change.manifestUrl = v.manifestUrl.trim();
      if (v.lifetime !== initial.lifetime) change.accessLifetimeMinutes = Number(v.lifetime);
      if (listOf(v.discord).join(",") !== listOf(initial.discord).join(",")) change.discordApplications = listOf(v.discord);
      if (v.actForDiscord !== initial.actForDiscord) change.actForDiscord = v.actForDiscord;
      if (Object.keys(change).length) await onSave(change);
    },
  });
  const off = !editable || busy;

  return (
    <SettingsSection icon="settings" title="Settings"
      action={editable && form.isDirty ? (
        <Button variant="primary" size="sm" busy={busy} disabled={busy} onClick={form.submit}>Save</Button>
      ) : null}>
      <Form form={form} className="admin-section-fields">
        <FormField name="name" label="Name" control="input" wide={false} className="admin-field" disabled={off} />
        <FormField name="audience" label="Audience" control="input" wide={false} className="admin-field" mono spellCheck="false"
          autoCapitalize="off" disabled={off} />
        <FormField name="manifestUrl" label="Manifest" hint={manifestText(app) || undefined} control="input" mono className="admin-field"
          spellCheck="false" autoCapitalize="off" disabled={off} />
        <FormField name="lifetime" label="Access lifetime (minutes)" control="input" wide={false} type="number"
          className="admin-field admin-field--short" disabled={off} />
        <FormField name="discord" label="Discord applications" hint="Whose tokens this application’s clients may present."
          control="input" mono className="admin-field" spellCheck="false" autoCapitalize="off" disabled={off} />
      </Form>
      <SettingsRow icon="link" title="Acts for linked Discord users">
        <Toggle on={form.values.actForDiscord} disabled={off} label="Acts for linked Discord users"
          onChange={(on) => form.setValue("actForDiscord", on)} />
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
