// PermissionPage — one permission: its name, the actions filed into it, the roles holding it, and
// deleting it.
//
// Reached at `#/permissions/<id>` from the Permissions list. Filing an action into a permission gives it
// to every role holding the permission, so the rules are asked whether the actions may be set to what
// they are, and a permission they close stays on screen with their reason. A self action is every
// active person's already and is filed nowhere.

import React from "react";

import {
  Breadcrumbs, Button, Dialog, Icon, IconButton, List, ListRow, PageHeader, Pill, Select, SettingsRow,
  SettingsSection,
} from "@thekrystalship/krystal-ui";
import { applicationOf, useApplicationNames } from "./catalogParts.jsx";
import { Brief, Locked, RefusalNote, authorityGate, componentOf, roleName, useAuthority, useChecks } from "./kit.jsx";
import { NameDialog, countOf } from "./roleParts.jsx";
import { go } from "./route.js";
import { authorityStore, editRefusal } from "./stores.js";

function PermissionPage({ id }) {
  const state = useAuthority();
  const view = state.view;
  const applicationName = useApplicationNames();
  const permission = view ? view.permissions.find((p) => p.id === id) : null;
  const canEdit = !editRefusal("permission.create");

  const verdicts = useChecks(
    permission ? [{ kind: "permission.actions", permissionId: permission.id, actions: permission.actions }] : [],
    permission ? permission.id + ":" + permission.actions.join(",") : "");
  const verdict = verdicts ? verdicts[0] : null;
  const closed = !canEdit || (!!verdict && !verdict.allowed);

  const [refusal, setRefusal] = React.useState(null);
  const [busy, setBusy] = React.useState(false);
  const [renaming, setRenaming] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);

  const gate = authorityGate(state);
  if (gate) return gate;
  if (!permission) {
    return (
      <Brief icon="key-round" title="No such permission"
        actions={<Button variant="outline" icon="arrow-left" onClick={() => go("permissions")}>Back to permissions</Button>} />
    );
  }

  const run = async (change) => {
    setBusy(true);
    setRefusal(null);
    const r = await authorityStore.edit(change);
    setBusy(false);
    if (!r.ok) setRefusal(r.refusal);
    return r;
  };
  const setActions = (actions) => run({ kind: "permission.actions", permissionId: permission.id, actions });

  // An action filed here that no member declares any more is still listed, so it can be taken out.
  const filed = permission.actions
    .map((action) => view.catalog.find((a) => a.action === action) || { action, title: "No longer declared", undeclared: true })
    .sort((a, b) => a.action.localeCompare(b.action));

  // What can still be filed here: every declared action that is not a self action and is not in it.
  const fileable = view.catalog.filter((a) => !a.self && !permission.actions.includes(a.action));
  const components = [...new Set(fileable.map((a) => componentOf(a.action)))].sort();

  const rename = async (name) => {
    const r = await authorityStore.edit({ kind: "permission.rename", permissionId: permission.id, name });
    if (r.ok) setRenaming(false);
    return r;
  };

  return (
    <>
      <Breadcrumbs items={[{ label: "Permissions", href: "#/permissions" }, { label: permission.name }]}
        onNavigate={() => go("permissions")} />
      <PageHeader title={permission.name} sub={countOf(permission.actions.length, "action")}
        actions={!closed ? (
          <>
            <Button variant="outline" icon="pencil" disabled={busy} onClick={() => setRenaming(true)}>Rename</Button>
            <Button variant="outline" tone="danger" icon="trash-2" disabled={busy} onClick={() => setDeleting(true)}>Delete</Button>
          </>
        ) : null} />
      <RefusalNote refusal={refusal} />

      <SettingsSection icon="list-checks" title="Actions" action={closed ? <Locked result={verdict} /> : null}>
        {filed.length === 0 ? <div className="admin-quiet">No action is filed into this permission.</div> : (
          <List label="Actions filed" className="admin-flat-list">
            {filed.map((a) => (
              <ListRow key={a.action} data-action={a.action}
                lead={<Icon name="list-checks" size={16} />}
                title={a.title}
                sub={a.action}
                meta={a.undeclared ? null : (
                  <>
                    <Pill size="sm">{applicationName(applicationOf(a))}</Pill>
                    {componentOf(a.action).toLowerCase() !== applicationName(applicationOf(a)).toLowerCase() && (
                      <Pill size="sm">{componentOf(a.action)}</Pill>
                    )}
                  </>
                )}
                actions={!closed ? (
                  <IconButton variant="ghost" size="sm" icon="x" label={"Take " + a.action + " out"} disabled={busy}
                    onClick={() => setActions(permission.actions.filter((x) => x !== a.action))} />
                ) : null} />
            ))}
          </List>
        )}
        {closed && verdict && !verdict.allowed && <div className="admin-quiet">{verdict.message}</div>}
        {!closed && (
          <div className="admin-section-form">
            <Select value="" aria-label="File an action into this permission" disabled={busy || fileable.length === 0}
              onChange={(e) => { if (e.target.value) setActions([...permission.actions, e.target.value]); }}>
              <option value="">{fileable.length === 0 ? "Every action is filed here" : "File an action…"}</option>
              {components.map((c) => (
                <optgroup key={c} label={c}>
                  {fileable.filter((a) => componentOf(a.action) === c)
                    .sort((a, b) => a.action.localeCompare(b.action))
                    .map((a) => <option key={a.action} value={a.action}>{a.action}</option>)}
                </optgroup>
              ))}
            </Select>
            <Button variant="outline" icon="list-filter" href="#/catalog?unmapped=1"
              onClick={(e) => { e.preventDefault(); go("catalog", null, { unmapped: "1" }); }}>
              Unmapped actions
            </Button>
          </div>
        )}
      </SettingsSection>

      <SettingsSection icon="shield" title="Held by">
        {permission.heldBy.length === 0 && <div className="admin-quiet">No role holds this permission.</div>}
        {permission.heldBy.map((rid) => (
          <SettingsRow key={rid} icon="shield" title={roleName(view, rid)}>
            <Button variant="outline" size="sm" onClick={() => go("roles", rid)}>Open</Button>
          </SettingsRow>
        ))}
      </SettingsSection>

      {renaming && (
        <NameDialog kind="permission" title="Rename permission" icon="pencil" initial={permission.name}
          submit="Rename" onSubmit={rename} onClose={() => setRenaming(false)} />
      )}
      {deleting && <DeleteDialog permission={permission} onClose={() => setDeleting(false)} />}
    </>
  );
}

function DeleteDialog({ permission, onClose }) {
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);

  const remove = async () => {
    setBusy(true);
    setRefusal(null);
    const r = await authorityStore.edit({ kind: "permission.delete", permissionId: permission.id });
    if (r.ok) { go("permissions"); return; }
    setBusy(false);
    setRefusal(r.refusal);
  };

  return (
    <Dialog title={`Delete ${permission.name}?`} icon="trash-2" onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="danger" icon="trash-2" busy={busy} onClick={remove}>{busy ? "Deleting…" : "Delete permission"}</Button>
        </>
      )}>
      <div data-dialog="delete-permission" className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <p className="admin-dialog-text">
          This removes the permission from every role holding it and leaves its actions unmapped. It cannot be undone.
        </p>
      </div>
    </Dialog>
  );
}

export { PermissionPage };
