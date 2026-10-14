// roleParts — what the Roles list and the role page share: the roles a person may be given, the rules'
// verdicts on editing them, and the dialog that names one. The permissions' pages use the dialog too.

import { FormField, FormSubmit } from "@thekrystalship/krystal";
import { FormDialog } from "../chrome/FormDialog.jsx";
import { useRefusingForm } from "./kit.jsx";

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
  const { form, notice } = useRefusingForm({
    defaultValues: { name: initial || "" },
    validate: { name: (v) => (v.trim() ? null : "Required") },
    onSubmit: async ({ name }) => {
      const r = await onSubmit(name.trim());
      if (!r.ok) return { refusal: r.refusal };
    },
  });
  return (
    <FormDialog form={form} name={kind + "-name"} title={title} icon={icon} onClose={onClose} notice={notice}
      submit={<FormSubmit>{submit}</FormSubmit>}>
      <FormField name="name" label="Name" control="input" autoFocus />
    </FormDialog>
  );
}

export { NameDialog, OWNER_VERDICT, PEOPLES, countOf, peoplesRoles, probeOf };
