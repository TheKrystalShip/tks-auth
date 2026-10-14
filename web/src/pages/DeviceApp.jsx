import React from "react";
import { Button, CardScreen, EmptyState, Form, FormField, FormSubmit, Input, Notice, useForm } from "@thekrystalship/krystal";
import { getJson, postJson } from "./api.js";

// DeviceApp — signing a device in with the code it shows.
//
// The code arrives in the address when the person scanned the device's QR code, or is typed. Either
// way nothing is approved until the page has shown which application the device asks for and what the
// device said it is, and the person has pressed one of the two buttons: a code in the address proves
// only that a link was opened.
//
// The name the device sent (what its owner calls it) is offered in a field the person may change; what
// they leave there is the name the device is signed in under, and an emptied field signs it in unnamed.
//
// Every call is same-origin and authenticated by the provider's cookie. A browser that is not signed in
// is sent back to /device, which signs it in and returns here with the code.

const MARK = import.meta.env.BASE_URL + "assets/tks-mark.png";

const CODE_CHARS = /[^BCDFGHJKLMNPQRSTVWXZ]/g;

// What the person typed, as the device shows it: uppercase, two groups of four.
function formatCode(typed) {
  const bare = typed.toUpperCase().replace(CODE_CHARS, "").slice(0, 8);
  return bare.length > 4 ? bare.slice(0, 4) + "-" + bare.slice(4) : bare;
}

function signInAgain(code) {
  window.location.assign("/device" + (code ? "?user_code=" + encodeURIComponent(code) : ""));
}

// The code, typed or pasted, formatted as the device shows it.
function CodeForm({ initial, error, onClearError, onLook }) {
  const form = useForm({
    defaultValues: { code: formatCode(initial) },
    rules: { code: { required: true, minLength: { value: 9, message: "The code is eight letters." } } },
    onSubmit: async ({ code }) => { await onLook(code); },
  });
  return (
    <Form form={form}>
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <FormField name="code" label="Code" control={({ value, onChange, onBlur, invalid, disabled, describedBy }) => (
        <Input
          id="device-code" wide mono
          className="device-code"
          type="text"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck="false"
          placeholder="XXXX-XXXX"
          value={value}
          invalid={invalid}
          disabled={disabled}
          aria-describedby={describedBy}
          onBlur={onBlur}
          onChange={(e) => { onChange(formatCode(e.target.value)); onClearError(); }} />
      )} />
      <FormSubmit size="lg" wide busyLabel="Checking…">Continue</FormSubmit>
    </Form>
  );
}

// What the device asked for, and the person's answer. The name is offered for them to change.
function DecideForm({ device, who, error, busy, onDecide }) {
  const form = useForm({
    defaultValues: { name: device.name || "" },
    rules: { name: { maxLength: 64 } },
    onSubmit: ({ name }) => onDecide(true, name),
  });
  return (
    <Form form={form}>
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <dl className="device-summary">
        <dt>Application</dt><dd>{device.application}</dd>
        <dt>Account</dt><dd>{who}</dd>
        <dt>Code</dt><dd className="device-summary__code">{device.userCode}</dd>
        {device.userAgent ? (<><dt>Device</dt><dd className="device-summary__agent">{device.userAgent}</dd></>) : null}
      </dl>
      <FormField name="name" label="Name" control="input" autoComplete="off" disabled={busy} />
      <FormSubmit size="lg" wide>Sign in</FormSubmit>
      <Button variant="outline" tone="danger" size="lg" wide disabled={busy || form.isSubmitting}
        onClick={() => onDecide(false, form.values.name)}>Refuse</Button>
    </Form>
  );
}

function DeviceApp() {
  const initial = new URLSearchParams(window.location.search).get("user_code") || "";
  const [account, setAccount] = React.useState(null);
  const [device, setDevice] = React.useState(null);
  const [done, setDone] = React.useState(null);
  const [error, setError] = React.useState(null);
  const [busy, setBusy] = React.useState(false);

  const look = React.useCallback(async (value) => {
    setError(null);
    const r = await getJson("/device/context" + (value ? "?user_code=" + encodeURIComponent(value) : ""));
    if (r.status === 401) { signInAgain(value); return; }
    if (!r.ok) { setDevice(null); setError(r.error); return; }
    setAccount(r.body.account);
    setDevice(r.body.device || null);
  }, []);

  React.useEffect(() => { look(formatCode(initial)); }, [look, initial]);

  const decide = async (approve, name) => {
    setBusy(true);
    setError(null);
    const r = await postJson("/device/decision", { userCode: device.userCode, approve, name });
    setBusy(false);
    if (r.status === 401) { signInAgain(device.userCode); return; }
    if (!r.ok) { setError(r.error); return; }
    setDone(r.body);
  };

  const who = account ? (account.displayName || account.username) : null;

  if (done) {
    const approved = done.decision === "approved";
    return (
      <CardScreen mark={MARK} wordmark="The Krystal Ship">
        <EmptyState
          icon={approved ? "check" : "x"}
          tone={approved ? "ok" : "neutral"}
          title={approved ? "Device signed in" : "Device refused"}
          actions={approved ? <Button variant="primary" href="/account#/devices">Your devices</Button> : null}>
          <p>{done.application}</p>
        </EmptyState>
      </CardScreen>
    );
  }

  return (
    <CardScreen mark={MARK} wordmark="The Krystal Ship" tagline="Sign in a device.">
      {device
        ? <DecideForm device={device} who={who} error={error} busy={busy} onDecide={decide} />
        : <CodeForm initial={initial} error={error} onClearError={() => error && setError(null)} onLook={look} />}
    </CardScreen>
  );
}

export { DeviceApp };
