import React from "react";
import { Icon } from "@thekrystalship/krystal-ui";
import { AuthError, AuthShell } from "../chrome/AuthChrome.jsx";
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

const CODE_CHARS = /[^BCDFGHJKLMNPQRSTVWXZ]/g;

// What the person typed, as the device shows it: uppercase, two groups of four.
function formatCode(typed) {
  const bare = typed.toUpperCase().replace(CODE_CHARS, "").slice(0, 8);
  return bare.length > 4 ? bare.slice(0, 4) + "-" + bare.slice(4) : bare;
}

function signInAgain(code) {
  window.location.assign("/device" + (code ? "?user_code=" + encodeURIComponent(code) : ""));
}

function DeviceApp() {
  const initial = new URLSearchParams(window.location.search).get("user_code") || "";
  const [code, setCode] = React.useState(formatCode(initial));
  const [account, setAccount] = React.useState(null);
  const [device, setDevice] = React.useState(null);
  const [name, setName] = React.useState("");
  const [done, setDone] = React.useState(null);
  const [error, setError] = React.useState(null);
  const [busy, setBusy] = React.useState(false);

  const look = React.useCallback(async (value) => {
    setBusy(true);
    setError(null);
    const r = await getJson("/device/context" + (value ? "?user_code=" + encodeURIComponent(value) : ""));
    setBusy(false);
    if (r.status === 401) { signInAgain(value); return; }
    if (!r.ok) { setDevice(null); setError(r.error); return; }
    setAccount(r.body.account);
    setDevice(r.body.device || null);
    setName((r.body.device && r.body.device.name) || "");
  }, []);

  React.useEffect(() => { look(formatCode(initial)); }, [look, initial]);

  const decide = async (approve) => {
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
      <AuthShell tagline={null}>
        <div className="pending">
          <div className="pending__icon">
            <Icon name={approved ? "check" : "x"} size={26} strokeWidth={1.7} />
          </div>
          <h1 className="pending__title">{approved ? "Device signed in" : "Device refused"}</h1>
          <p className="pending__body">{done.application}</p>
        </div>
      </AuthShell>
    );
  }

  if (device) {
    return (
      <AuthShell tagline="Sign in a device.">
        <div className="login-card">
          <AuthError>{error}</AuthError>
          <dl className="device-summary">
            <dt>Application</dt><dd>{device.application}</dd>
            <dt>Account</dt><dd>{who}</dd>
            <dt>Code</dt><dd className="device-summary__code">{device.userCode}</dd>
            {device.userAgent ? (<><dt>Device</dt><dd className="device-summary__agent">{device.userAgent}</dd></>) : null}
          </dl>
          <label className="login-form__label" htmlFor="device-name">Name</label>
          <input
            id="device-name"
            className="login-form__input"
            type="text"
            autoComplete="off"
            maxLength={64}
            value={name}
            disabled={busy}
            onChange={(e) => setName(e.target.value)} />
          <div className="device-actions">
            <button type="button" className="login-form__submit" disabled={busy} onClick={() => decide(true)}>
              Sign in
            </button>
            <button type="button" className="host-btn host-btn--ghost" disabled={busy} onClick={() => decide(false)}>
              Refuse
            </button>
          </div>
        </div>
      </AuthShell>
    );
  }

  const submit = (e) => {
    e.preventDefault();
    if (code.length === 9) look(code);
  };

  return (
    <AuthShell tagline="Sign in a device.">
      <div className="login-card">
        <form className="login-form" onSubmit={submit}>
          <AuthError field>{error}</AuthError>
          <label className="login-form__label" htmlFor="device-code">Code</label>
          <input
            id="device-code"
            className="login-form__input device-code"
            type="text"
            autoComplete="off"
            autoCapitalize="characters"
            spellCheck="false"
            placeholder="XXXX-XXXX"
            value={code}
            disabled={busy}
            onChange={(e) => { setCode(formatCode(e.target.value)); if (error) setError(null); }} />
          <button type="submit" className="login-form__submit" disabled={busy || code.length !== 9}>
            {busy ? (<><span className="oauth-spinner" /> Checking…</>) : "Continue"}
          </button>
        </form>
      </div>
    </AuthShell>
  );
}

export { DeviceApp };
