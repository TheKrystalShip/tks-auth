import React from "react";
import {
  Button, CardScreen, FormField, Notice, SignInScreen, providerLabel, useForm,
} from "@thekrystalship/krystal";
import { PASSWORD_MIN, usernameProblem } from "../lib/credentialRules.js";
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

// `onSubmit({ username, password, displayName })` resolves `{ ok, error, unreachable }`. On `ok` the
// card stays inert: the page is navigating, and a card that re-enabled itself would offer a second
// submission of the same credential. Switching tabs keeps what was typed: somebody who tried to sign
// in and finds they have no account should not retype their username to make one.
function SignInCard({
  tab, onTab, providers, registrationOpen, available, providerError, onProviderError, onSubmit, onProvider, foot,
}) {
  const registering = tab === "register";
  const [leaving, setLeaving] = React.useState(false);
  const [busyProvider, setBusyProvider] = React.useState(null);

  const form = useForm({
    defaultValues: { username: "", displayName: "", password: "", confirm: "" },
    mode: "touched",
    rules: registering
      ? { username: { required: true }, password: { required: true, minLength: { value: PASSWORD_MIN, message: "At least " + PASSWORD_MIN + " characters." } }, confirm: { required: "Confirm the password" } }
      : { username: { required: true }, password: { required: true } },
    validate: registering
      ? {
        username: (v) => usernameProblem(v),
        confirm: (v, all) => (v !== all.password ? "Passwords don’t match" : null),
      }
      : {},
    onSubmit: async (values) => {
      if (onProviderError) onProviderError(null);
      const result = await onSubmit({
        username: registering ? values.username.trim() : values.username,
        password: values.password,
        displayName: values.displayName.trim(),
      });
      if (result.ok) { setLeaving(true); return; }
      if (result.unreachable && onProviderError) onProviderError(result.error);
      else return { message: result.error };
    },
  });

  // A refusal belongs to the tab it answered.
  React.useEffect(() => { form.setFormError(null); }, [tab]); // eslint-disable-line react-hooks/exhaustive-deps

  const bounce = (provider) => {
    setBusyProvider(provider);
    onProvider(provider);
  };

  const closed = registering && !registrationOpen;
  const { username, password, confirm } = form.values;

  return (
    <SignInScreen
      mark={MARK}
      wordmark="The Krystal Ship"
      tagline={registering ? "Create an account." : "Sign in."}
      foot={foot}
      error={providerError}
      tabs={TABS} tab={tab} onTab={onTab}
      providers={providers}
      busyProvider={busyProvider}
      onProvider={bounce}
      providerText={(p) => (providers.length === 1
        ? (registering ? "Sign up" : "Continue") + " with " + providerLabel(p)
        : providerLabel(p))}
      notice={closed ? <Notice tone="warn" icon="ban">This provider isn’t taking new accounts.</Notice> : null}
      password={!closed}
      divider="or with another account"
      form={form}
      busy={!available || leaving || !!providerError}
      passwordAutoComplete={registering ? "new-password" : "current-password"}
      passwordAside={!registering ? (
        <Button variant="link" size="xs"
          onClick={() => form.setFormError("Ask whoever manages accounts here to reset it.")}>
          Forgot?
        </Button>
      ) : null}
      submitLabel={registering ? "Create account" : "Sign in"}
      busyLabel={registering ? "Creating your account…" : "Signing in…"}
      usernameExtra={registering ? (
        <FormField name="displayName" label="Display name" aside="optional" control="input"
          autoComplete="nickname" placeholder={username.trim()} />
      ) : null}
      passwordHint={registering ? <PasswordStrength value={password} /> : null}
      extraFields={registering ? (
        <FormField name="confirm" label="Confirm password" control="password" autoComplete="new-password"
          hint={confirm && confirm === password ? "Passwords match" : null} />
      ) : null} />
  );
}

function SignInApp() {
  const [context, setContext] = React.useState(null);
  const [problem, setProblem] = React.useState(null);
  const [tab, setTab] = React.useState("login");
  const [providerError, setProviderError] = React.useState(null);
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
    <SignInCard
      tab={tab}
      onTab={setTab}
      providers={context ? context.providers || [] : []}
      registrationOpen={!!(context && context.registration)}
      available={!!context}
      providerError={providerError}
      onProviderError={setProviderError}
      onSubmit={submit}
      onProvider={(provider) => window.location.assign("/authorize/" + encodeURIComponent(provider))}
      foot={name ? <span>{registering ? "Creating your account for" : "Signing in to"} <b className="signin-foot__name">{name}</b></span> : null} />
  );
}

export { SignInApp };
