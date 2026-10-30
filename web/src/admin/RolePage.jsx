// RolePage — one role: its name, the permissions it holds, who holds it, and deleting it.
//
// Reached at `#/roles/<id>` from the Roles list. The permissions are every permission there is, each
// checked when the role holds it; the checks are one edit, saved together, because the rules judge a
// role's permissions as a set. The rules are asked whether that set may be written, and a role they
// close stays on screen with their reason. Owner holds every action and is edited by nobody;
// `everyone` holds what every active person holds and is edited like any role's but never renamed or
// deleted.

import React from "react";

import {
  Breadcrumbs, Button, Checkbox, ConfirmDialog, PageHeader, Select, SettingsRow, SettingsSection,
} from "@thekrystalship/krystal";
import { useApplications } from "./applicationsStore.js";
import { Brief, Locked, RefusalNote, authorityGate, confirmed, useAuthority, useChecks, useScopeText } from "./kit.jsx";
import { NameDialog, OWNER_VERDICT, countOf, holdsText, probeOf } from "./roleParts.jsx";
import { go } from "./route.js";
import { authorityStore, editRefusal } from "./stores.js";

function RolePage({ id }) {
  const state = useAuthority();
  const view = state.view;
  const scopeText = useScopeText();
  const applications = useApplications();
  const role = view ? view.roles.find((r) => r.id === id) : null;
  const canEdit = !editRefusal("role.create");
  const isOwnerRole = !!role && role.kind === "owner";

  const verdicts = useChecks(role && !isOwnerRole ? [probeOf(role)] : [], role ? role.id + ":" + role.permissions.join(",") : "");
  const verdict = isOwnerRole ? OWNER_VERDICT : verdicts ? verdicts[0] : null;
  const closed = isOwnerRole || !canEdit || (!!verdict && !verdict.allowed);

  const [refusal, setRefusal] = React.useState(null);
  const [busy, setBusy] = React.useState(false);
  const [renaming, setRenaming] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [chosen, setChosen] = React.useState(() => new Set(role ? role.permissions : []));
  React.useEffect(() => { setChosen(new Set(role ? role.permissions : [])); }, [role]);

  const gate = authorityGate(state);
  if (gate) return gate;
  if (!role || !["owner", "custom", "everyone"].includes(role.kind)) {
    return (
      <Brief icon="shield-off" title="No such role"
        actions={<Button variant="outline" icon="arrow-left" onClick={() => go("roles")}>Back to roles</Button>} />
    );
  }

  const custom = role.kind === "custom";
  const dirty = chosen.size !== role.permissions.length || role.permissions.some((p) => !chosen.has(p));
  const toggle = (pid, on) => setChosen((s) => { const n = new Set(s); if (on) n.add(pid); else n.delete(pid); return n; });

  const run = async (change) => {
    setBusy(true);
    setRefusal(null);
    const r = await authorityStore.edit(change);
    setBusy(false);
    if (!r.ok) setRefusal(r.refusal);
    return r;
  };

  const holders = view.assignments.filter((a) => a.roleId === role.id)
    .map((a) => ({ a, who: view.accounts.find((x) => x.id === a.accountId) }))
    .filter((h) => h.who);

  const rename = async (name) => {
    const r = await authorityStore.edit({ kind: "role.rename", roleId: role.id, name });
    if (r.ok) setRenaming(false);
    return r;
  };

  return (
    <>
      <Breadcrumbs items={[{ label: "Roles", href: "#/roles" }, { label: role.name }]} onNavigate={() => go("roles")} />
      <PageHeader title={role.name}
        sub={isOwnerRole ? "Every action" : holdsText(role)}
        actions={custom && !closed ? (
          <>
            <Button variant="outline" icon="pencil" disabled={busy} onClick={() => setRenaming(true)}>Rename</Button>
            <Button variant="outline" tone="danger" icon="trash-2" disabled={busy} onClick={() => setDeleting(true)}>Delete</Button>
          </>
        ) : null} />
      <RefusalNote refusal={refusal} />

      <SettingsSection icon="key-round" title="Permissions"
        action={closed ? <Locked result={verdict} /> : dirty ? (
          <Button variant="primary" size="sm" busy={busy} disabled={busy}
            onClick={() => run({ kind: "role.permissions", roleId: role.id, permissionIds: [...chosen] })}>
            Save permissions
          </Button>
        ) : null}>
        {isOwnerRole && <SettingsRow icon="crown" title="Every action" sub={OWNER_VERDICT.message} />}
        {!isOwnerRole && view.permissions.length === 0 && <div className="admin-quiet">No permissions exist yet.</div>}
        {!isOwnerRole && view.permissions.map((p) => (
          <SettingsRow key={p.id} icon="key" title={p.name} sub={countOf(p.actions.length, "action")}>
            <Checkbox checked={chosen.has(p.id)} disabled={closed || busy} aria-label={p.name}
              onChange={(on) => toggle(p.id, on)} />
          </SettingsRow>
        ))}
        {closed && verdict && !isOwnerRole && <div className="admin-quiet">{verdict.message}</div>}
      </SettingsSection>

      {custom && (
        <SettingsSection icon="app-window" title="Every action of an application">
          <SettingsRow icon="app-window" title="Application">
            <Select value={role.holdsEveryActionOf || ""} aria-label="Application whose every action this role holds"
              disabled={closed || busy || !applications.list}
              onChange={(e) => run({ kind: "role.every-action", roleId: role.id, application: e.target.value || null })}>
              <option value="">None</option>
              {(applications.list || []).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
            </Select>
          </SettingsRow>
        </SettingsSection>
      )}

      <SettingsSection icon="users" title="Held by">
        {role.kind === "everyone" && <SettingsRow icon="users" title="Every active person" />}
        {role.kind !== "everyone" && holders.length === 0 && <div className="admin-quiet">Nobody holds this role.</div>}
        {role.kind !== "everyone" && holders.map(({ a, who }) => (
          <SettingsRow key={a.id} icon={who.kind === "service" ? "bot" : "user"} title={who.displayName || who.username}
            sub={scopeText(a.scope)}>
            {who.kind === "person" && (
              <Button variant="outline" size="sm" onClick={() => go("accounts", who.id)}>Open</Button>
            )}
          </SettingsRow>
        ))}
      </SettingsSection>

      {renaming && (
        <NameDialog title="Rename role" icon="pencil" initial={role.name} submit="Rename" onSubmit={rename}
          onClose={() => setRenaming(false)} />
      )}
      {deleting && (
        <DeleteDialog role={role} onClose={() => setDeleting(false)} />
      )}
    </>
  );
}

function DeleteDialog({ role, onClose }) {
  return (
    <ConfirmDialog data-dialog="delete-role" title={`Delete ${role.name}?`} icon="trash-2" tone="danger"
      confirm="Delete role" busyLabel="Deleting…" onClose={onClose}
      onConfirm={async () => {
        const r = await authorityStore.edit({ kind: "role.delete", roleId: role.id });
        if (r.ok) go("roles");
        return confirmed(r);
      }}>
      This removes the role. It cannot be undone.
    </ConfirmDialog>
  );
}

export { RolePage };
