// Assignments — who holds which role where, and giving or taking one away.
//
// One editor for both places an assignment is made: an account's page (its roles at every scope) and
// the Assignments page (every role held at one scope). Fix `accountId` for the first, `scope` for the
// second; the other is chosen.
//
// Every role is offered, and each one the rules would refuse at the chosen account and scope carries
// the provider's reason — someone who may assign roles sees why a role above their own cannot be
// chosen, instead of finding it missing. What a role grants at a narrower scope than some of its
// actions is less than it grants everywhere, which the rules decide and the provider states; this
// editor says only what it was told.

import React from "react";

import { AccountAvatar, Button, Dialog, IconButton, List, ListRow, Notice, Pill, Select } from "@thekrystalship/krystal";
import { go } from "./route.js";
import { authorityStore, editRefusal } from "./stores.js";
import { Locked, RefusalNote, parseInstance, roleName, useChecks, useScopeOptions, useScopeText } from "./kit.jsx";

// A scope as the target an assignment edit is asked at.
function targetOf(scope) {
  if (!scope || scope === "cluster") return { cluster: true };
  if (scope.startsWith("node:")) return { hostId: scope.slice(5) };
  const inst = parseInstance(scope);
  return inst ? { server: { hostId: inst.hostId, id: inst.serverId, installNonce: inst.nonce } } : { cluster: true };
}

function Assignments({ view, accountId, scope }) {
  const scopeText = useScopeText();
  const scopeOptions = useScopeOptions(view);
  const [refusal, setRefusal] = React.useState(null);
  const [busy, setBusy] = React.useState(false);
  const [revoking, setRevoking] = React.useState(null);

  const held = view.assignments.filter((a) =>
    (!accountId || a.accountId === accountId) && (!scope || a.scope === scope));

  const people = view.accounts.filter((a) => a.kind === "person" && a.status === "active")
    .sort((a, b) => a.username.localeCompare(b.username));
  const roles = view.roles.filter((r) => r.kind === "custom" || r.kind === "owner")
    .sort((a, b) => a.rank - b.rank);

  const [who, setWho] = React.useState(accountId || "");
  const [where, setWhere] = React.useState(scope || "cluster");
  const [role, setRole] = React.useState("");
  const account = accountId || who;
  const at = scope || where;
  const assignRefusal = editRefusal("assign", targetOf(at));
  const canAssign = !assignRefusal;

  // The rules' verdict on each role for the chosen account and scope, and on revoking each held one.
  const probes = React.useMemo(() => [
    ...(account && canAssign ? roles.map((r) => ({ kind: "assign", accountId: account, roleId: r.id, scope: at })) : []),
    ...held.map((a) => ({ kind: "revoke", assignmentId: a.id })),
  ], [account, at, canAssign, roles, held]);
  const verdicts = useChecks(probes, probes.map((p) => p.kind + (p.roleId || p.assignmentId)).join("|") + account + at);
  const offered = account && canAssign ? roles.length : 0;
  const verdictForRole = (id) => {
    const i = roles.findIndex((r) => r.id === id);
    return verdicts && i >= 0 && i < offered ? verdicts[i] : null;
  };
  const verdictForRevoke = (id) => {
    const i = held.findIndex((a) => a.id === id);
    return verdicts && i >= 0 ? verdicts[offered + i] : null;
  };

  const run = async (change) => {
    setBusy(true);
    setRefusal(null);
    const r = await authorityStore.edit(change);
    setBusy(false);
    if (!r.ok) setRefusal(r.refusal);
    return r;
  };

  const chosen = verdictForRole(role);
  const nameOf = (id) => { const a = view.accounts.find((x) => x.id === id); return a ? (a.displayName || a.username) : id; };

  return (
    <div className="admin-assign">
      <RefusalNote refusal={refusal} compact />
      {held.length === 0
        ? <div className="admin-quiet">{scope ? "No roles held here." : "Holds only everyone."}</div>
        : (
          <List label="Roles held" className="admin-assign__list">
            {held.map((a) => {
              const v = verdictForRevoke(a.id);
              const closed = v && !v.allowed;
              const name = roleName(view, a.roleId);
              const holder = view.accounts.find((x) => x.id === a.accountId);
              const reason = closed && v.message ? <> · {v.message}</> : null;
              return (
                <ListRow key={a.id} data-assignment={name}
                  lead={accountId ? undefined : (
                    <AccountAvatar user={{ display: nameOf(a.accountId), name: holder ? holder.username : a.accountId }} size={32} />
                  )}
                  title={accountId ? name : nameOf(a.accountId)}
                  sub={accountId
                    ? <>{scopeText(a.scope)}{reason}</>
                    : <>{holder ? holder.username : a.accountId}{reason}</>}
                  meta={<>
                    {!accountId && <Pill size="sm">{name}</Pill>}
                    {closed ? <Locked result={v} /> : null}
                  </>}
                  onOpen={accountId ? undefined : () => go("accounts", a.accountId)}
                  actions={!closed && !editRefusal("revoke", targetOf(a.scope)) ? (
                    <IconButton variant="ghost" tone="danger" icon="x" label={"Take " + name + " away"}
                      disabled={busy} onClick={() => setRevoking(a)} />
                  ) : null} />
              );
            })}
          </List>
        )}

      {(canAssign || !scope) && (
        <form className="admin-assign__form" onSubmit={async (e) => {
          e.preventDefault();
          if (!account || !role) return;
          const r = await run({ kind: "assign", accountId: account, roleId: role, scope: at });
          if (r.ok) setRole("");
        }}>
          {!accountId && (
            <Select value={who} aria-label="Account" disabled={busy} onChange={(e) => setWho(e.target.value)}>
              <option value="">Account…</option>
              {people.map((p) => <option key={p.id} value={p.id}>{p.displayName || p.username}</option>)}
            </Select>
          )}
          {!scope && (
            <Select value={where} aria-label="Scope" disabled={busy} onChange={(e) => setWhere(e.target.value)}>
              {scopeOptions.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </Select>
          )}
          <Select value={role} aria-label="Role" disabled={busy || !canAssign || !account} onChange={(e) => setRole(e.target.value)}>
            <option value="">Role…</option>
            {roles.map((r) => {
              const v = verdictForRole(r.id);
              const closed = v && !v.allowed;
              return <option key={r.id} value={r.id} disabled={!!closed}>{r.name}{closed ? " — " + v.message : ""}</option>;
            })}
          </Select>
          <Button type="submit" variant="primary" busy={busy}
            disabled={busy || !canAssign || !account || !role || (chosen && !chosen.allowed)}>
            Assign
          </Button>
        </form>
      )}
      {!canAssign && account && (
        <Notice tone="warn" icon="lock" className="admin-refusal admin-refusal--compact">
          Assigning roles at {scopeText(at)} · {assignRefusal}
        </Notice>
      )}

      {revoking && (
        <Dialog title={"Take " + roleName(view, revoking.roleId) + " away?"} icon="user-minus" canClose={!busy}
          onClose={() => setRevoking(null)}
          foot={(
            <>
              <Button variant="outline" disabled={busy} onClick={() => setRevoking(null)}>Keep</Button>
              <Button variant="danger" busy={busy} onClick={async () => {
                await run({ kind: "revoke", assignmentId: revoking.id });
                setRevoking(null);
              }}>Take away</Button>
            </>
          )}>
          <p className="admin-dialog-text" data-dialog="confirm-take-away">
            {nameOf(revoking.accountId)} stops holding {roleName(view, revoking.roleId)} at {scopeText(revoking.scope)}.
          </p>
        </Dialog>
      )}
    </div>
  );
}

export { Assignments };
