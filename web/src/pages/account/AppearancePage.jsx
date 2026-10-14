import { PageHeader, SettingsSection, ThemePicker } from "@thekrystalship/krystal";

// The one appearance preference of this browser, shared by every page on this origin.
function AppearancePage() {
  return (
    <>
      <PageHeader title="Appearance" />
      <SettingsSection icon="palette" title="Theme">
        <ThemePicker />
      </SettingsSection>
    </>
  );
}

export { AppearancePage };
