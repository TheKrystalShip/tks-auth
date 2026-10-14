import React from "react";
import {
  Button, CardScreen, Field, Input, Notice, OptionList, PasswordInput, ProviderButton, providerLabel,
} from "@thekrystalship/krystal-ui";
import { passwordOk, usernameOk, usernameProblem } from "../lib/credentialRules.js";
import { getJson, postJson } from "./api.js";
import { PasswordStrength } from "./PasswordStrength.jsx";

// SignInApp — the sign-in page, for the request in flight.
//
// What is particular to this request — whose sign-in it is, which providers are wired, whether
// registration is open — is read from /authorize/context, because the document is static and the
// request lives behind a cookie. A credential answers with where to go: the client, carrying its
// code, or the wait. Nothing here ever holds a session.
//
// One card, two tabs. The order down the card is the same on both, so nothing moves when you toggle:
// tabs, providers, divider, form. A wrong password renders above the username, inside the form, where
// the eye already is on the way back to fixing it. A door that cannot be reached invalidates the tabs,
// the providers and the form alike, so that one sits at the top of the card.

const MARK = import.meta.env.BASE_URL + "assets/tks-mark.png";

const TABS = [
  { value: "login", label: "Sign in" },
  { value: "register", label: "Register" },
];

// The providers wired here, in the order the provider reports them. One is a branded full-width
// button; several are a neutral 2-up grid, because stacked brand fills imply a recommendation nobody
// made.
function ProviderButtons({ providers, registering, busy, disabled, onPick }) {
  if (!providers.length) return null;

  if (providers.length === 1) {
    const p = providers[0];
    return (
      <ProviderButton
        provider={p} brand wide size="lg"
        busy={busy === p}
        disabled={disabled}
        onClick={() => onPick(p)}>
        {busy === p
          ? "Redirecting to " + providerLabel(p) + "…"
          : (registering ? "Sign up" : "Continue") + " with " + providerLabel(p)}
      </ProviderButton>
    );
  }

  return (
    <div className="signin-providers">
      {providers.map((p) => (
        <ProviderButton
          key={p} provider={p} wide
          busy={busy === p}
          disabled={disabled}
          onClick={() => onPick(p)}>
          {providerLabel(p) + (busy === p ? "…" : "")}
        </ProviderButton>
      ))}
    </div>
  );
}

// `onSubmit({ username, password, displayName })` resolves `{ ok, error, unreachable }`. On `ok` the
// card stays busy: the page is navigating, and a card that re-enabled itself would offer a second
// submission of the same credential.
function SignInCard({
  tab, onTab, providers, registrationOpen, available, anchorError, onAnchorError, onSubmit, onProvider,
}) {
  const registering = tab === "register";

  const [username, setUsername] = React.useState("");
  const [displayName, setDisplayName] = React.useState("");
  const [password, setPassword] = React.useState("");
  const [confirm, setConfirm] = React.useState("");
  const [busy, setBusy] = React.useState(false);
  const [formError, setFormError] = React.useState(null);

  // Switching tabs keeps what was typed: somebody who tried to sign in and finds they have no account
  // should not retype their username to make one.
  React.useEffect(() => { setFormError(null); }, [tab]);

  const nameProblem = registering && username ? usernameProblem(username) : null;
  const confirmMismatch = registering && confirm.length > 0 && confirm !== password;
  const confirmMatch = registering && confirm.length > 0 && confirm === password;

  const canSubmit = !busy && available && (registering
    ? usernameOk(username) && passwordOk(password) && confirm === password
    : !!username && !!password);

  const submit = async (e) => {
    e.preventDefault();
    if (!canSubmit) return;
    setBusy(true);
    setFormError(null);
    if (onAnchorError) onAnchorError(null);

    const result = await onSubmit({
      username: registering ? username.trim() : username,
      password,
      displayName: displayName.trim(),
    });

    if (!result.ok) {
      setBusy(false);
      if (result.unreachable && onAnchorError) onAnchorError(result.error);
      else setFormError(result.error);
    }
  };

  const bounce = (provider) => {
    if (busy || !available) return;
    setBusy(provider);
    onProvider(provider);
  };

  const unreachable = !!anchorError;
  const locked = unreachable || !!busy;

  return (
    <>
      {anchorError ? <Notice tone="danger">{anchorError}</Notice> : null}

      <OptionList variant="segmented" label="Sign in or register" value={tab} onChange={onTab}
        disabled={!!busy} options={TABS} />

      <ProviderButtons
        providers={providers}
        registering={registering}
        busy={typeof busy === "string" ? busy : null}
        disabled={unreachable || !!busy}
        onPick={bounce} />

      {registering && !registrationOpen ? (
        <Notice tone="warn" icon="ban">This cluster isn’t taking new accounts.</Notice>
      ) : (
        <>
          {providers.length ? (
            <div className="signin-divider"><span>or with a KGSM account</span></div>
          ) : null}

          <form className="signin-form" onSubmit={submit}>
            {formError ? <Notice tone="danger">{formError}</Notice> : null}

            <Field label="Username" error={nameProblem}>
              <Input
                id="auth-username" wide
                type="text"
                autoComplete="username"
                autoCapitalize="off"
                spellCheck="false"
                value={username}
                invalid={!!nameProblem}
                disabled={locked}
                onChange={(e) => { setUsername(e.target.value); if (formError) setFormError(null); }} />
            </Field>

            {registering ? (
              <Field label="Display name" aside="optional">
                <Input
                  id="auth-display" wide
                  type="text"
                  autoComplete="nickname"
                  placeholder={username.trim()}
                  value={displayName}
                  disabled={locked}
                  onChange={(e) => setDisplayName(e.target.value)} />
              </Field>
            ) : null}

            <Field
              label="Password"
              aside={!registering ? (
                <Button
                  variant="link" size="xs"
                  onClick={() => setFormError("Ask whoever manages accounts on this cluster to reset it.")}>
                  Forgot?
                </Button>
              ) : null}>
              <PasswordInput
                id="auth-password" wide
                aria-label="Password"
                autoComplete={registering ? "new-password" : "current-password"}
                value={password}
                disabled={locked}
                onChange={(e) => { setPassword(e.target.value); if (formError) setFormError(null); }} />
              {registering ? <PasswordStrength value={password} /> : null}
            </Field>

            {registering ? (
              <Field
                label="Confirm password"
                error={confirmMismatch ? "Passwords don’t match" : null}
                hint={confirmMatch ? "Passwords match" : null}>
                <PasswordInput
                  id="auth-confirm" wide
                  aria-label="Confirm password"
                  autoComplete="new-password"
                  value={confirm}
                  invalid={confirmMismatch}
                  disabled={locked}
                  onChange={(e) => setConfirm(e.target.value)} />
              </Field>
            ) : null}

            <Button type="submit" variant="primary" size="lg" wide busy={busy === true} disabled={!canSubmit}>
              {busy === true
                ? (registering ? "Creating your account…" : "Signing in…")
                : (registering ? "Create account" : "Sign in")}
            </Button>
          </form>
        </>
      )}
    </>
  );
}

function SignInApp() {
  const [context, setContext] = React.useState(null);
  const [problem, setProblem] = React.useState(null);
  const [tab, setTab] = React.useState("login");
  const [anchorError, setAnchorError] = React.useState(null);
  const registering = tab === "register";

  React.useEffect(() => {
    let live = true;
    getJson("/authorize/context").then((r) => {
      if (!live) return;
      if (r.ok) setContext(r.body);
      else setProblem(r.error);
    });
    return () => { live = false; };
  }, []);

  const submit = async ({ username, password, displayName }) => {
    const r = registering
      ? await postJson("/authorize/register", { username, password, displayName: displayName || null })
      : await postJson("/authorize/credentials", { username, password });
    if (!r.ok) return r;

    window.location.assign((r.body && (r.body.redirect || r.body.wait)) || "/authorize/wait");
    return { ok: true };
  };

  if (problem) {
    return (
      <CardScreen mark={MARK} wordmark="The Krystal Ship">
        <Notice tone="danger">{problem}</Notice>
      </CardScreen>
    );
  }

  // The provider's own pages are not announced: "Signing in to your account" says nothing the page
  // does not.
  const name = context && !context.client.own ? context.client.name : null;

  return (
    <CardScreen
      mark={MARK}
      wordmark="The Krystal Ship"
      tagline={registering ? "Create an account." : "Sign in."}
      foot={name ? <span>{registering ? "Creating your account for" : "Signing in to"} <b>{name}</b></span> : null}>
      <SignInCard
        tab={tab}
        onTab={setTab}
        providers={context ? context.providers || [] : []}
        registrationOpen={!!(context && context.registration)}
        available={!!context}
        anchorError={anchorError}
        onAnchorError={setAnchorError}
        onSubmit={submit}
        onProvider={(provider) => window.location.assign("/authorize/" + encodeURIComponent(provider))} />
    </CardScreen>
  );
}

export { SignInApp };
