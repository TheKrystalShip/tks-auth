// kit.jsx — what the admin pages share: reading the authority, asking the rules about edits nobody has
// made, and showing a refusal.
//
// Every page gates its controls on the request they would make (`stores.js`) and shows a control the
// rules would refuse with the rules' own reason beside it, rather than hiding it. The reason is the
// provider's: `authorityStore.check` runs the same rules an edit meets and writes nothing.

import React from "react";

import { Button, EmptyState, Notice, Pill, Skeleton, useForm, useStore } from "@thekrystalship/krystal";
import { authorityStore, refusalOf } from "./stores.js";

// The authority, read when the first page wanting it mounts and kept for the rest.
function useAuthority() {
  const state = useStore(authorityStore);
  React.useEffect(() => { authorityStore.refresh().catch(() => {}); }, []);
  return state;
}

// The rules' verdicts on `edits`, re-asked whenever `key` or the authority's version moves. Null
// until answered, and a null entry for an edit the provider could not be asked about.
function useChecks(edits, key) {
  const version = useStore(authorityStore, (s) => (s.view ? s.view.version : null));
  const [results, setResults] = React.useState(null);
  const ref = React.useRef(edits);
  ref.current = edits;
  React.useEffect(() => {
    let cancelled = false;
    if (version == null) return undefined;
    authorityStore.check(ref.current).then((r) => { if (!cancelled) setResults(r); });
    return () => { cancelled = true; };
  }, [key, version]);
  return results;
}

// A refusal beside the control it is about. `reauth_required` is the one a person can clear here: the
// account page, opened at `#confirm`, asks for their credential, and the change can be made again.
function RefusalNote({ refusal, compact }) {
  if (!refusal) return null;
  const reauth = refusal.code === "reauth_required";
  return (
    <Notice tone="warn" icon="lock" className={"admin-refusal" + (compact ? " admin-refusal--compact" : "")}>
      {refusal.message}
      {refusal.actions && refusal.actions.length > 0 && (
        <> <span className="admin-refusal__actions">{refusal.actions.join(", ")}</span></>
      )}
      {reauth && (
        <> <Button variant="link" size="sm" href="/account#confirm" target="_blank" rel="noreferrer">Prove it’s you</Button></>
      )}
    </Notice>
  );
}

// A form whose submission is a change against the provider. `onSubmit(values)` makes it and resolves
// nothing, or `{ refusal }`, or throws; the provider's refusal stands beside the fields as a
// RefusalNote, with the form still open for another try.
function useRefusingForm(options) {
  const [refusal, setRefusal] = React.useState(null);
  const form = useForm({
    ...options,
    onSubmit: async (values) => {
      setRefusal(null);
      try {
        const r = await options.onSubmit(values);
        if (r && r.refusal) setRefusal(r.refusal);
      } catch (e) {
        setRefusal(refusalOf(e));
      }
    },
  });
  return { form, notice: <RefusalNote refusal={refusal} compact /> };
}

// What a ConfirmDialog's `onConfirm` resolves from a change that answers `{ ok, refusal }`: the refusal
// as a RefusalNote, so its reauth link and action list show in the confirm.
const confirmed = (r) => (r.ok ? undefined : { refusal: <RefusalNote refusal={r.refusal} compact /> });

// A lock on a row the rules close, the provider's reason in its tooltip. The row's own `sub` carries
// the reason in words, where a sentence has room.
function Locked({ result }) {
  if (!result || result.allowed) return null;
  return <Pill tone="muted" icon="lock" size="sm" title={result.message || undefined}>Locked</Pill>;
}

// The component an action belongs to: the part before the colon.
const componentOf = (action) => String(action || "").split(":")[0];

// A role's name as a person reads it.
function roleName(view, id) {
  const r = view && view.roles.find((x) => x.id === id);
  return r ? r.name : id;
}

// The pages' shared empty states.
function Brief({ title, sub, icon, tone, actions }) {
  return <EmptyState icon={icon} tone={tone} title={title} actions={actions}>{sub}</EmptyState>;
}

// A list on its way: rows' worth of placeholder.
function Loading() {
  return (
    <div className="admin-loading" role="status" aria-label="Loading">
      <Skeleton h={44} r={10} />
      <Skeleton h={44} r={10} />
      <Skeleton h={44} r={10} />
    </div>
  );
}

// What a page renders while the authority is not in hand, or null once it is.
function authorityGate(state) {
  if (state.view) return null;
  if (state.status === "error") {
    const e = state.error;
    if (e && e.status === 403) return <Brief icon="lock" title="You don’t have access to this" sub={e.userMessage} />;
    return <Brief icon="wifi-off" tone="warn" title="Couldn’t read access" sub={(e && e.userMessage) || null} />;
  }
  return <Loading />;
}

export {
  Brief, Loading, Locked, RefusalNote, authorityGate, componentOf, confirmed, roleName,
  useAuthority, useChecks, useRefusingForm,
};
