import { Button, PageHeader, SettingsRow, SettingsSection } from "@thekrystalship/krystal";
import { deviceIcon, deviceName } from "../../lib/devices.js";
import { when } from "../../lib/formatting.js";

// Where this account is signed in: browsers, applications and devices, each ended or renamed here.
function DevicesPage({ me, busy, onRename, onEnd, onEndAll }) {
  return (
    <>
      <PageHeader title="Devices & sessions" />
      <SettingsSection icon="monitor-smartphone" title="Signed in"
        action={me.sessions.length > 1 ? (
          <Button variant="outline" size="sm" busy={busy === "all"} onClick={onEndAll}>Sign out everywhere</Button>
        ) : null}>
        {me.sessions.map((row) => (
          <SettingsRow key={row.sid}
            icon={deviceIcon(row)}
            title={row.current ? "This browser" : row.name || deviceName(row.userAgent)}
            sub={(row.kind === "provider" ? "Signed in here" : row.kind === "device" ? "A device" : "An application") + " · " + when(row.created)}>
            {row.kind === "device" ? (
              <Button variant="outline" size="sm" disabled={busy === row.sid} onClick={() => onRename(row)}>Rename</Button>
            ) : null}
            <Button variant="outline" size="sm" disabled={busy === row.sid} title={row.userAgent || undefined}
              onClick={() => onEnd(row)}>
              {row.current ? "Sign out" : "End"}
            </Button>
          </SettingsRow>
        ))}
      </SettingsSection>
    </>
  );
}

export { DevicesPage };
