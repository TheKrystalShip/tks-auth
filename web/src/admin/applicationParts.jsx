// applicationParts — what the Applications list and the application page share: reading a list typed
// into a field, a change run against the provider, and the fields of a client and the dialogs that
// take them.

import { FormField, FormSubmit } from "@thekrystalship/krystal";
import { FormDialog } from "../chrome/FormDialog.jsx";
import { api } from "./client.js";
import { useRefusingForm } from "./kit.jsx";
import { refusalOf } from "./stores.js";

// A list typed one per line or separated by commas or spaces.
const listOf = (text) => String(text || "").split(/[\s,]+/).map((s) => s.trim()).filter(Boolean);

// A call against the provider as `{ ok, answer }` or `{ ok: false, refusal }`.
async function attempt(call) {
  try {
    return { ok: true, answer: await call() };
  } catch (e) {
    return { ok: false, refusal: refusalOf(e) };
  }
}

const EMPTY_CLIENT = { clientId: "", redirects: "", postLogout: "", confidential: false, device: false };

// One client's registration, as the form's values hold it. An empty id is made by the provider.
const clientBody = (v) => ({
  clientId: v.clientId.trim() || null,
  redirectUris: listOf(v.redirects),
  postLogoutRedirectUris: listOf(v.postLogout),
  confidential: v.confidential,
  device: v.device,
});

// The fields of a client, in a form whose values include `EMPTY_CLIENT`'s names.
function ClientFields({ prefix }) {
  return (
    <>
      <FormField name="clientId" label="Client id" aside="optional" control="input" mono spellCheck="false"
        autoCapitalize="off" id={prefix + "-client"} />
      <FormField name="redirects" label="Redirect URIs" control="textarea" mono spellCheck="false" rows={3}
        id={prefix + "-redirects"} />
      <FormField name="postLogout" label="Post-sign-out URIs" control="textarea" mono spellCheck="false" rows={2}
        id={prefix + "-post-logout"} />
      <FormField name="confidential" label="Confidential" control="checkbox" />
      <FormField name="device" label="Device sign-in" control="checkbox" />
    </>
  );
}

// Adding a client to an application.
function ClientDialog({ applicationId, onDone, onClose }) {
  const { form, notice } = useRefusingForm({
    defaultValues: EMPTY_CLIENT,
    onSubmit: async (v) => {
      const r = await attempt(() => api.applications.addClient(applicationId, clientBody(v)));
      if (r.ok) onDone(r.answer);
      else return { refusal: r.refusal };
    },
  });
  return (
    <FormDialog form={form} name="client-add" title="Add a client" icon="plus" onClose={onClose} notice={notice}
      submit={<FormSubmit>Add client</FormSubmit>}>
      <ClientFields prefix="new" />
    </FormDialog>
  );
}

// Registering an application with its first client.
function RegisterDialog({ onDone, onClose }) {
  const { form, notice } = useRefusingForm({
    defaultValues: {
      id: "", name: "", audience: "", manifestUrl: "", lifetime: "", discord: "", actForDiscord: false, ...EMPTY_CLIENT,
    },
    rules: { id: { required: true } },
    validate: { id: (v) => (v.trim() ? null : "Required") },
    onSubmit: async (v) => {
      const id = v.id.trim();
      const client = clientBody(v);
      const hasClient = client.clientId || client.redirectUris.length || client.confidential || client.device;
      const r = await attempt(() => api.applications.add({
        id,
        name: v.name.trim() || id,
        audience: v.audience.trim() || null,
        manifestUrl: v.manifestUrl.trim() || null,
        accessLifetimeMinutes: v.lifetime ? Number(v.lifetime) : null,
        discordApplications: listOf(v.discord),
        actForDiscord: v.actForDiscord,
        clients: hasClient ? [client] : [],
      }));
      if (r.ok) onDone(id, r.answer);
      else return { refusal: r.refusal };
    },
  });
  const id = form.values.id.trim();
  return (
    <FormDialog form={form} name="application-register" title="Register an application" icon="app-window"
      onClose={onClose} notice={notice} submit={<FormSubmit>Register</FormSubmit>}>
      <FormField name="id" label="Id" control="input" mono autoFocus spellCheck="false" autoCapitalize="off" id="app-id" />
      <FormField name="name" label="Name" control="input" id="app-name" />
      <FormField name="audience" label="Audience" aside="optional" control="input" mono spellCheck="false"
        autoCapitalize="off" placeholder={id} id="app-audience" />
      <FormField name="manifestUrl" label="Manifest" aside="optional" control="input" mono spellCheck="false"
        autoCapitalize="off" id="app-manifest" />
      <FormField name="lifetime" label="Access lifetime (minutes)" aside="optional" control="input" type="number"
        placeholder="5" id="app-lifetime" />
      <FormField name="discord" label="Discord applications" aside="optional" control="input" mono spellCheck="false"
        autoCapitalize="off" id="app-discord" />
      <FormField name="actForDiscord" label="Acts for linked Discord users" control="checkbox" />
      <h3 className="admin-form__title">First client</h3>
      <ClientFields prefix="register" />
    </FormDialog>
  );
}

export { ClientDialog, RegisterDialog, attempt, listOf };
