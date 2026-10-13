import { PageHeader, SettingsRow, SettingsSection } from "@thekrystalship/krystal-ui";

const capitalise = (word) => (word ? word[0].toUpperCase() + word.slice(1) : "—");

// Who the account is, as the provider holds it.
function ProfilePage({ me }) {
  return (
    <>
      <PageHeader title="Profile" />
      <SettingsSection icon="user" title="Account">
        <SettingsRow icon="user" title="Display name" sub={me.displayName} />
        <SettingsRow icon="at-sign" title="Username" sub={me.username} />
        <SettingsRow icon="shield" title="Status"
          sub={me.status === "pending" ? "Awaiting approval" : capitalise(me.status)} />
      </SettingsSection>
    </>
  );
}

export { ProfilePage };
