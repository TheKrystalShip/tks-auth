// accountParts — what the Accounts list and the account page share: reading the accounts, the status a
// person stands in as a pill, and the providers an account signs in with.
//
// The accounts are read straight from the provider rather than through a store: they change only when
// somebody changes them here, and a cached list is a list that can be stale about who may sign in.
// Each write re-reads, and the authority (which holds the roles) is read again with them.

import React from "react";

import { Pill } from "@thekrystalship/krystal-ui";
import { api } from "./client.js";
import { authorityStore, refusalOf } from "./stores.js";

const STATUS = {
  pending: { tone: "warn", label: "Awaiting approval" },
  active: { tone: "ok", label: "Active" },
  disabled: { tone: "muted", label: "Disabled" },
};

function StatusPill({ status }) {
  const s = STATUS[status];
  return <Pill size="sm" tone={s ? s.tone : "neutral"} data-status={status}>{s ? s.label : status}</Pill>;
}

// The providers an account signs in with, from the credential handles it holds (`provider:subject`).
const providersOf = (u) => (u.identities || []).map((i) => (typeof i === "string" ? i.split(":")[0] : i.provider));

// A credential as the provider names it, and the account it is at there.
const handleOf = (i) => (typeof i === "string" ? i : i.handle);

const nameOf = (u) => u.displayName || u.username;

// The accounts, `null` until read, and the refusal that kept them from being. `reload` reads them and
// the authority again, resolving when both calls are made.
function useAccounts() {
  const [rows, setRows] = React.useState(null);
  const [refusal, setRefusal] = React.useState(null);

  const reload = React.useCallback(() => {
    setRefusal(null);
    authorityStore.refresh().catch(() => {});
    return api.users.list().then(
      (list) => setRows(list),
      (e) => { setRows([]); setRefusal(refusalOf(e)); });
  }, []);

  React.useEffect(() => { reload(); }, [reload]);
  return { rows, refusal, setRefusal, reload };
}

export { StatusPill, handleOf, nameOf, providersOf, useAccounts };
