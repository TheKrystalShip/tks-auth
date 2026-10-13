import { Button, PageHeader, SettingsRow, SettingsSection } from "@thekrystalship/krystal-ui";
import { fmtRelative, parseTs } from "../../lib/formatting.js";

function when(ts) {
  if (!ts) return "—";
  try { return fmtRelative(parseTs(ts)); } catch { return "—"; }
}

// A person reads a device, not a user-agent string. Browser and platform only, and the raw string as
// the tooltip, because the heuristic is a label and not a claim.
function deviceName(ua) {
  if (!ua) return "Unknown device";
  const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox"
    : /Chrome\//.test(ua) ? "Chrome" : /Safari\//.test(ua) ? "Safari" : null;
  const os = /Android/.test(ua) ? "Android" : /iPhone|iPad/.test(ua) ? "iOS" : /Windows/.test(ua) ? "Windows"
    : /Mac OS X/.test(ua) ? "macOS" : /Linux/.test(ua) ? "Linux" : null;
  if (browser && os) return browser + " on " + os;
  return browser || os || "Unknown device";
}

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
            icon={row.kind === "provider" ? "globe" : row.kind === "device" ? "tv" : /mobile|android|iphone|ipad/i.test(row.userAgent || "") ? "smartphone" : "monitor"}
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
