// PermissionsPage — the permissions: make one and open one for the actions filed into it.
//
// Editing a permission changes every role holding it, so the rules close one held by a role ranked at
// or above the caller's. Each permission is asked about once per version, and a closed one carries the
// provider's reason.

import React from "react";

import { Button, EmptyState, Icon, List, ListRow, PageHeader, Pill } from "@thekrystalship/krystal";
import { Locked, authorityGate, roleName, useAuthority, useChecks } from "./kit.jsx";
import { NameDialog, countOf } from "./roleParts.jsx";
import { go } from "./route.js";
import { authorityStore, editRefusal } from "./stores.js";

const ROLES_SHOWN = 3;

function PermissionsPage() {
  const state = useAuthority();
  const view = state.view;
  const canEdit = !editRefusal("permission.create");
  const [creating, setCreating] = React.useState(false);

  const permissions = React.useMemo(
    () => (view ? [...view.permissions].sort((a, b) => a.name.localeCompare(b.name)) : []), [view]);
  const probes = React.useMemo(
    () => permissions.map((p) => ({ kind: "permission.actions", permissionId: p.id, actions: p.actions })),
    [permissions]);
  const verdicts = useChecks(probes, probes.map((p) => p.permissionId + ":" + p.actions.join(",")).join("|"));

  const gate = authorityGate(state);
  if (gate) return gate;

  const create = async (name) => {
    const r = await authorityStore.edit({ kind: "permission.create", name });
    if (r.ok) setCreating(false);
    return r;
  };

  return (
    <>
      <PageHeader title="Permissions"
        actions={canEdit ? <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>New permission</Button> : null} />

      {permissions.length === 0 ? <EmptyState icon="key-round" title="No permissions yet" /> : (
        <List label="Permissions">
          {permissions.map((p, i) => {
            const verdict = verdicts ? verdicts[i] : null;
            const held = p.heldBy.map((id) => roleName(view, id));
            return (
              <ListRow key={p.id} data-permission={p.name}
                lead={<Icon name="key-round" size={16} />}
                title={p.name}
                sub={countOf(p.actions.length, "action")}
                meta={<>
                  {held.slice(0, ROLES_SHOWN).map((r) => <Pill key={r} size="sm">{r}</Pill>)}
                  {held.length > ROLES_SHOWN && <Pill size="sm">+{held.length - ROLES_SHOWN}</Pill>}
                  {canEdit && <Locked result={verdict} />}
                </>}
                onOpen={() => go("permissions", p.id)} />
            );
          })}
        </List>
      )}

      {creating && (
        <NameDialog kind="permission" title="New permission" icon="key-round" submit="Create permission"
          onSubmit={create} onClose={() => setCreating(false)} />
      )}
    </>
  );
}

export { PermissionsPage };
