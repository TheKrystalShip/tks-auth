// devices.js — a session as a person reads it: the device's name and the icon for its kind.

// A person reads a device, not a user-agent string. Browser and platform only, and the raw string is
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

// The icon for a session: a browser's sign-in at the provider, a device signed in with a code, a phone
// or a desktop.
function deviceIcon(row) {
  if (row.kind === "provider") return "globe";
  if (row.kind === "device") return "tv";
  return /mobile|android|iphone|ipad/i.test(row.userAgent || "") ? "smartphone" : "monitor";
}

// What kind of sign-in a session is, in a word.
function deviceKind(row) {
  return row.kind === "provider" ? "Browser" : row.kind === "device" ? "Device" : "Application";
}

export { deviceIcon, deviceKind, deviceName };
