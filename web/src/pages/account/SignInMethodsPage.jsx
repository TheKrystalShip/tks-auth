import { Button, PageHeader, ProviderButton, SettingsRow, SettingsSection, providerLabel } from "@thekrystalship/krystal-ui";

// The password and the provider accounts attached to this one. Each change goes through `guarded`,
// which asks for a recent proof first when the last one has aged.
function SignInMethodsPage({ me, busy, guarded }) {
  const linked = new Set(me.identities.map((i) => i.provider));
  const connectable = me.providers.filter((p) => !linked.has(p));

  return (
    <>
      <PageHeader title="Sign-in methods" />
      <SettingsSection icon="key-round" title="Password">
        <SettingsRow icon="lock" title="Password" sub={me.hasPassword ? "Set" : "None"}>
          <Button variant="outline" size="sm" onClick={() => guarded({ kind: "password" })}>
            {me.hasPassword ? "Change" : "Set a password"}
          </Button>
        </SettingsRow>
      </SettingsSection>

      {me.identities.length || connectable.length ? (
        <SettingsSection icon="link" title="Connected accounts">
          {me.identities.map((identity) => (
            <SettingsRow key={identity.id} icon="link" title={providerLabel(identity.provider)}
              sub={identity.label || identity.handle}>
              <Button variant="outline" size="sm" disabled={busy === identity.id}
                onClick={() => guarded({ kind: "unlink", identity })}>
                Disconnect
              </Button>
            </SettingsRow>
          ))}
          {connectable.map((provider) => (
            <SettingsRow key={provider} icon="plus" title={providerLabel(provider)} sub="Not connected">
              <ProviderButton provider={provider} size="sm" busy={busy === "link:" + provider}
                onClick={() => guarded({ kind: "link", provider })}>
                Connect
              </ProviderButton>
            </SettingsRow>
          ))}
        </SettingsSection>
      ) : null}
    </>
  );
}

export { SignInMethodsPage };
