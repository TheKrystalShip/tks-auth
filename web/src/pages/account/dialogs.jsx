// The four dialogs of the account document. Each is a form: Enter submits, Cancel closes, and the
// provider's refusal stands in the dialog as a danger Notice.

import {
  ConfirmDialog, FormField, FormSubmit, ProviderButton, providerLabel, useForm,
} from "@thekrystalship/krystal";
import { FormDialog } from "../../chrome/FormDialog.jsx";
import { PASSWORD_MIN } from "../../lib/credentialRules.js";
import { deleteJson, postJson } from "../api.js";
import { PasswordStrength } from "../PasswordStrength.jsx";

// Renaming a device signed in to this account: the name it is listed by here and carries on its tokens,
// which the device reads at its next refresh. An emptied field takes the name away.
function RenameDialog({ row, onClose, onDone }) {
  const form = useForm({
    defaultValues: { name: row.name || "" },
    rules: { name: { maxLength: 64 } },
    onSubmit: async ({ name }) => {
      const r = await postJson("/account/sessions/name", { sid: row.sid, name });
      if (r.ok) onDone();
      else return { message: r.error };
    },
  });
  return (
    <FormDialog form={form} name="rename" title="Rename device" icon="tv" onClose={onClose}
      submit={<FormSubmit busyLabel="Saving…">Save</FormSubmit>}>
      <FormField name="name" label="Name" control="input" autoComplete="off" autoFocus />
    </FormDialog>
  );
}

// Asking for a proof. The password where the account has one; otherwise a provider it already holds,
// which is a round trip rather than a field.
function ProveDialog({ me, onClose, onProved, onProvider }) {
  const providers = me.identities.map((i) => i.provider).filter((p) => me.providers.includes(p));
  const form = useForm({
    defaultValues: { password: "" },
    rules: { password: { required: true } },
    onSubmit: async ({ password }) => {
      const r = await postJson("/account/reauth", { password });
      if (r.ok) onProved();
      else return { message: r.error };
    },
  });
  return (
    <FormDialog form={form} name="prove" title="Confirm it’s you" icon="shield-check" onClose={onClose}
      submit={me.hasPassword ? <FormSubmit busyLabel="Checking…">Confirm</FormSubmit> : null}>
      {me.hasPassword ? (
        <FormField name="password" label="Password" control="password" autoComplete="current-password" autoFocus />
      ) : (
        <div className="account-body__stack">
          {providers.map((p) => (
            <ProviderButton key={p} provider={p} brand wide size="lg" onClick={() => onProvider(p)}>
              Continue with {providerLabel(p)}
            </ProviderButton>
          ))}
        </div>
      )}
    </FormDialog>
  );
}

function PasswordDialog({ hasPassword, onClose, onReauth, onDone }) {
  const form = useForm({
    defaultValues: { next: "", again: "" },
    mode: "touched",
    rules: {
      next: { required: true, minLength: { value: PASSWORD_MIN, message: "At least " + PASSWORD_MIN + " characters." } },
      again: { required: "Type it again" },
    },
    validate: { again: (v, all) => (v !== all.next ? "Passwords don’t match" : null) },
    onSubmit: async ({ next }) => {
      const r = await postJson("/account/password", { password: next });
      if (r.ok) onDone();
      else if (r.code === "reauth_required") onReauth();
      else return { message: r.error };
    },
  });
  return (
    <FormDialog form={form} name="password" title={hasPassword ? "Change your password" : "Set a password"} icon="lock" onClose={onClose}
      submit={<FormSubmit busyLabel="Saving…">Save password</FormSubmit>}>
      <FormField name="next" label="New password" control="password" autoComplete="new-password" autoFocus
        hint={form.values.next ? <PasswordStrength value={form.values.next} /> : null} />
      <FormField name="again" label="New password again" control="password" autoComplete="new-password" />
    </FormDialog>
  );
}

function DisconnectDialog({ identity, onClose, onReauth, onDone }) {
  return (
    <ConfirmDialog title={"Disconnect " + providerLabel(identity.provider) + "?"} icon="unlink" tone="danger"
      confirm="Disconnect" busyLabel="Disconnecting…" onClose={onClose}
      onConfirm={async () => {
        const r = await deleteJson("/account/identities/" + encodeURIComponent(identity.id));
        if (r.ok) onDone();
        else if (r.code === "reauth_required") onReauth();
        else return { refusal: r.error };
      }}>
      {identity.label || identity.handle}
    </ConfirmDialog>
  );
}

export { DisconnectDialog, PasswordDialog, ProveDialog, RenameDialog };
