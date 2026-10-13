// RolesPage — the roles in rank order: make one, rank by dragging, and open one for its permissions.
//
// Owner is first and locked: it holds every action and is edited by nobody. `everyone` is last among
// the people's roles and holds what every active person holds; its permissions are edited like any
// role's. A role a service account is given is a service's own and is not listed here.
//
// Each role is asked about once per version — "may I set this role's permissions to what they are?" —
// and a role the rules close carries the provider's reason rather than being left clickable. Ranks are
// positions among the custom roles, the first being 1; the arrows move a role one place for those who
// cannot drag.

import React from "react";

import { Button, EmptyState, Icon, IconButton, List, ListRow, PageHeader } from "@thekrystalship/krystal-ui";
import { Locked, RefusalNote, authorityGate, useAuthority, useChecks } from "./kit.jsx";
import { NameDialog, OWNER_VERDICT, countOf, peoplesRoles, probeOf } from "./roleParts.jsx";
import { go } from "./route.js";
import { authorityStore, editRefusal } from "./stores.js";

function RolesPage() {
  const state = useAuthority();
  const view = state.view;
  const canEdit = !editRefusal("role.create");

  const roles = React.useMemo(() => peoplesRoles(view), [view]);
  const custom = roles.filter((r) => r.kind === "custom");

  const editable = React.useMemo(() => roles.filter((r) => r.kind !== "owner"), [roles]);
  const verdicts = useChecks(editable.map(probeOf), editable.map((r) => r.id + ":" + r.permissions.join(",")).join("|"));
  const verdictOf = (role) => {
    if (role.kind === "owner") return OWNER_VERDICT;
    const i = editable.findIndex((r) => r.id === role.id);
    return verdicts && i >= 0 ? verdicts[i] : null;
  };

  const [refusal, setRefusal] = React.useState(null);
  const [busy, setBusy] = React.useState(false);
  const [creating, setCreating] = React.useState(false);
  const [dragging, setDragging] = React.useState(null);

  const gate = authorityGate(state);
  if (gate) return gate;

  const moveTo = async (roleId, index) => {
    setBusy(true);
    setRefusal(null);
    const r = await authorityStore.edit({ kind: "role.rank", roleId, rank: index + 1 });
    setBusy(false);
    if (!r.ok) setRefusal(r.refusal);
  };

  const create = async (name) => {
    const r = await authorityStore.edit({ kind: "role.create", name });
    if (r.ok) setCreating(false);
    return r;
  };

  return (
    <>
      <PageHeader title="Roles"
        actions={canEdit ? <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>New role</Button> : null} />
      <RefusalNote refusal={refusal} />

      {roles.length === 0 ? <EmptyState icon="shield" title="No roles yet" /> : (
        <List label="Roles">
          {roles.map((role) => {
            const verdict = verdictOf(role);
            const closed = role.kind === "owner" || !canEdit || (verdict && !verdict.allowed);
            const index = custom.findIndex((c) => c.id === role.id);
            const draggable = role.kind === "custom" && !closed && !busy;
            const reason = closed && verdict && !verdict.allowed ? verdict.message : null;
            return (
              <ListRow key={role.id} data-role={role.name}
                className={dragging === role.id ? "admin-row--dragging" : draggable ? "admin-row--draggable" : undefined}
                draggable={draggable}
                onDragStart={() => setDragging(role.id)}
                onDragEnd={() => setDragging(null)}
                onDragOver={(e) => { if (dragging && role.kind === "custom") e.preventDefault(); }}
                onDrop={(e) => {
                  e.preventDefault();
                  if (dragging && dragging !== role.id && index >= 0) moveTo(dragging, index);
                  setDragging(null);
                }}
                lead={<Icon name={draggable ? "grip-vertical" : role.kind === "owner" ? "crown" : "shield"} size={16} />}
                title={role.name}
                sub={<>
                  {role.kind === "owner" ? "Every action" : countOf(role.permissions.length, "permission")}
                  {reason && role.kind !== "owner" && <> · {reason}</>}
                </>}
                meta={role.kind === "owner" || (canEdit && verdict && !verdict.allowed) ? <Locked result={verdict} /> : null}
                actions={role.kind === "custom" && !closed ? (
                  <>
                    <IconButton variant="ghost" size="sm" icon="chevron-up" label={"Move " + role.name + " up"}
                      disabled={busy || index <= 0} onClick={() => moveTo(role.id, index - 1)} />
                    <IconButton variant="ghost" size="sm" icon="chevron-down" label={"Move " + role.name + " down"}
                      disabled={busy || index >= custom.length - 1} onClick={() => moveTo(role.id, index + 1)} />
                  </>
                ) : null}
                onOpen={() => go("roles", role.id)} />
            );
          })}
        </List>
      )}

      {creating && (
        <NameDialog title="New role" icon="shield-plus" submit="Create role" onSubmit={create} onClose={() => setCreating(false)} />
      )}
    </>
  );
}

export { RolesPage };
