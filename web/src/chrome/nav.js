// nav.js — the places of the sidebar frame: which group each belongs to, which document draws it, and
// where it is.
//
// Pure data. The account document (`/account`) and the admin document (`/admin/`) are two pages that
// share one frame; a place in the other document is reached by loading that document at its address.

const DOCUMENTS = { account: "/account", admin: "/admin/" };

const GROUPS = [
  { id: "account", label: "Your account" },
  { id: "people", label: "People" },
  { id: "access", label: "Access" },
  { id: "applications", label: "Applications" },
];

// `gated`: shown only where the caller says it is offered (`Frame`'s `offered`).
const PLACES = [
  { group: "account", id: "profile", label: "Profile", icon: "circle-user", doc: "account", hash: "#/profile" },
  { group: "account", id: "sign-in", label: "Sign-in methods", icon: "log-in", doc: "account", hash: "#/sign-in" },
  { group: "account", id: "devices", label: "Devices & sessions", icon: "monitor-smartphone", doc: "account", hash: "#/devices" },
  { group: "account", id: "appearance", label: "Appearance", icon: "palette", doc: "account", hash: "#/appearance" },

  { group: "people", id: "overview", label: "Overview", icon: "layout-dashboard", doc: "admin", hash: "#/overview" },
  { group: "people", id: "accounts", label: "Accounts", icon: "users", doc: "admin", hash: "#/accounts" },
  { group: "people", id: "services", label: "Service requests", icon: "bot", doc: "admin", hash: "#/services" },

  { group: "access", id: "roles", label: "Roles", icon: "shield", doc: "admin", hash: "#/roles" },
  { group: "access", id: "permissions", label: "Permissions", icon: "key-round", doc: "admin", hash: "#/permissions" },
  { group: "access", id: "assignments", label: "Assignments", icon: "user-check", doc: "admin", hash: "#/assignments" },
  { group: "access", id: "catalog", label: "Catalog", icon: "list-checks", doc: "admin", hash: "#/catalog" },

  { group: "applications", id: "applications", label: "Applications", icon: "app-window", doc: "admin", hash: "#/applications", gated: true },
];

const placeOf = (id) => PLACES.find((p) => p.id === id) || null;

// Where a place is, from anywhere.
const hrefOf = (place) => DOCUMENTS[place.doc] + place.hash;

export { DOCUMENTS, GROUPS, PLACES, hrefOf, placeOf };
