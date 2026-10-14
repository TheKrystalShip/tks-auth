// roleParts — what the Roles list and the role page share: the roles a person may be given, the rules'
// verdicts on editing them, and the dialog that names one. The permissions' pages use the dialog too.

import React from "react";

import { Button, Dialog, Field, Input } from "@thekrystalship/krystal-ui";
import { RefusalNote } from "./kit.jsx";

const PEOPLES = new Set(["owner", "custom", "everyone"]);

// Owner holds every action and is edited by nobody; the rules are not asked about it.
const OWNER_VERDICT = { allowed: false, message: "Owner holds every action and is edited by nobody." };

// The roles of the people, in rank order.
const peoplesRoles = (view) => (view ? view.roles.filter((r) => PEOPLES.has(r.kind)).sort((a, b) => a.rank - b.rank) : []);

// "N permissions", singular where it is one.
const countOf = (n, unit) => n + " " + unit + (n === 1 ? "" : "s");

// The probe that asks whether a role's permissions may be set to what they are.
const probeOf = (role) => ({ kind: "role.permissions", roleId: role.id, permissionIds: role.permissions });

// A role's or permission's name (`kind`), asked for in a dialog: to make one, or to rename one.
// `onSubmit(name)` resolves to `{ ok, refusal }`; the dialog closes on success and shows the refusal
// otherwise.
function NameDialog({ kind = "role", title, icon, initial, submit, onSubmit, onClose }) {
  const [name, setName] = React.useState(initial || "");
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);
  const value = name.trim();

  const send = async (e) => {
    e.preventDefault();
    if (!value || busy) return;
    setBusy(true);
    setRefusal(null);
    const r = await onSubmit(value);
    if (r.ok) return;
    setBusy(false);
    setRefusal(r.refusal);
  };

  return (
    <Dialog title={title} icon={icon} as="form" onSubmit={send} onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" type="submit" busy={busy} disabled={!value}>{submit}</Button>
        </>
      )}>
      <div data-dialog={kind + "-name"} className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <Field label="Name">
          <Input id={kind + "-name"} wide value={name} autoFocus disabled={busy} onChange={(e) => setName(e.target.value)} />
        </Field>
      </div>
    </Dialog>
  );
}

export { NameDialog, OWNER_VERDICT, PEOPLES, countOf, peoplesRoles, probeOf };
