// ServicesPage — what each service account has asked to do as itself, and what was decided.
//
// A component declares the actions it performs on its own behalf; the provider approves each one
// automatically unless it is an `auth:*` action, which waits for a person. A person may approve a
// waiting one, narrow an approved one to a smaller scope, or revoke one — and a revoked requirement
// stays revoked for the account's life. A requirement the component's manifest stops listing is kept
// and grants nothing until it is listed again.

import React from "react";

import { Button, Dialog, EmptyState, List, ListRow, PageHeader, Pill, Select, SettingsSection } from "@thekrystalship/krystal-ui";
import { RefusalNote, authorityGate, useAuthority, useScopeOptions, useScopeText } from "./kit.jsx";
import { authorityStore, editRefusal } from "./stores.js";

const STATE = {
  approved: { tone: "ok", label: "Approved" },
  waiting: { tone: "warn", label: "Waiting" },
  revoked: { tone: "danger", label: "Revoked" },
};

const countOf = (n, noun) => n + " " + noun + (n === 1 ? "" : "s");

function ServicesPage() {
  const state = useAuthority();
  const view = state.view;
  const canManage = !editRefusal("requirement.approve");
  const scopeText = useScopeText();
  const scopes = useScopeOptions(view);
  const [refusal, setRefusal] = React.useState(null);
  const [busy, setBusy] = React.useState(false);
  const [revoking, setRevoking] = React.useState(null);

  const run = async (change) => {
    setBusy(true);
    setRefusal(null);
    const r = await authorityStore.edit(change);
    setBusy(false);
    if (!r.ok) setRefusal(r.refusal);
    return r;
  };

  const gate = authorityGate(state);
  const services = view
    ? view.accounts.filter((a) => a.kind === "service").sort((a, b) => a.username.localeCompare(b.username))
    : [];
  const waiting = services.reduce((n, s) => n + s.requirements.filter((q) => q.state === "waiting").length, 0);

  return (
    <>
      <PageHeader title="Service requests"
        sub={view && services.length > 0 ? countOf(services.length, "service") + (waiting ? " · " + waiting + " waiting" : "") : null} />
      {gate || (
        <>
          <RefusalNote refusal={refusal} />
          {services.length === 0 && <EmptyState icon="bot" title="No service accounts" />}
          {services.map((svc) => (
            <div key={svc.id} data-service={svc.username}>
              <SettingsSection icon="bot" title={svc.username}
                meta={svc.status !== "active" ? svc.status : countOf(svc.requirements.length, "requirement")}>
                {svc.requirements.length === 0
                  ? <div className="admin-quiet">Declares nothing.</div>
                  : (
                    <List label={svc.username + " requirements"} className="admin-flat-list">
                      {svc.requirements.map((q) => (
                        <Requirement key={q.action} svc={svc} q={q} canManage={canManage} busy={busy}
                          scopeText={scopeText} scopes={scopes} run={run} onRevoke={() => setRevoking({ svc, q })} />
                      ))}
                    </List>
                  )}
              </SettingsSection>
            </div>
          ))}
        </>
      )}

      {revoking && (
        <Dialog title={"Revoke " + revoking.q.action + "?"} icon="ban" canClose={!busy}
          onClose={() => setRevoking(null)}
          foot={(
            <>
              <Button variant="outline" disabled={busy} onClick={() => setRevoking(null)}>Keep</Button>
              <Button variant="danger" busy={busy} onClick={async () => {
                await run({ kind: "requirement.revoke", accountId: revoking.svc.id, action: revoking.q.action });
                setRevoking(null);
              }}>Revoke</Button>
            </>
          )}>
          <p className="admin-dialog-text" data-dialog="confirm-revoke-requirement">
            {revoking.svc.username} stops being able to do this as itself, and a revoked requirement stays revoked for the account’s life.
          </p>
        </Dialog>
      )}
    </>
  );
}

function Requirement({ svc, q, canManage, busy, scopeText, scopes, run, onRevoke }) {
  const [scope, setScope] = React.useState(q.grant || "cluster");
  const s = STATE[q.state];
  const decided = q.state === "approved" ? (q.decidedBy ? "by " + q.decidedBy : "automatically") : null;
  return (
    <ListRow data-requirement={q.action}
      title={q.action}
      sub={q.why || null}
      meta={<>
        <Pill size="sm" tone={s ? s.tone : "neutral"} data-state={q.state}>{s ? s.label : q.state}</Pill>
        {!q.declared && <Pill size="sm" tone="muted">no longer declared</Pill>}
      </>}
      aside={q.state === "approved" ? scopeText(q.grant) + " · " + decided : q.scopeKind}
      actions={canManage && q.state !== "revoked" ? (
        <>
          <Select value={scope} aria-label={"Scope for " + q.action} disabled={busy}
            onChange={(e) => setScope(e.target.value)}>
            {scopes.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </Select>
          {q.state === "waiting"
            ? <Button size="sm" variant="primary" disabled={busy}
                onClick={() => run({ kind: "requirement.approve", accountId: svc.id, action: q.action, scope })}>Approve</Button>
            : <Button size="sm" variant="outline" disabled={busy || scope === q.grant}
                onClick={() => run({ kind: "requirement.narrow", accountId: svc.id, action: q.action, scope })}>Narrow</Button>}
          <Button size="sm" variant="danger" disabled={busy} onClick={onRevoke}>Revoke</Button>
        </>
      ) : null} />
  );
}

export { ServicesPage };
