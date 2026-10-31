// client.js — the admin pages' calls: same-origin, to the provider's admin routes, through the
// session's `authorizedFetch`, which carries the bearer, renews a refused one and replays the call once.
//
// A refusal is thrown as the provider wrote it — `status`, `envCode`, `userMessage` and the body — so a
// page shows the provider's sentence beside the control it is about. A call the session cannot
// authorize at all is a 401 here; the session's own end is what the gate draws.

import { session } from "./auth.js";

class ApiError extends Error {
  constructor(status, body) {
    const envelope = body && body.error;
    super((envelope && envelope.message) || "The provider answered " + status + ".");
    this.status = status;
    this.envCode = (envelope && envelope.code) || null;
    this.userMessage = status === 0 ? "The provider didn’t answer." : this.message;
    this.body = body;
  }
}

async function call(method, path, body) {
  const headers = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  let res;
  try {
    res = await session.authorizedFetch(path, {
      method, headers, credentials: "omit",
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch (err) {
    throw new ApiError(err && (err.code === "ended" || err.code === "none") ? 401 : 0, null);
  }
  if (!res) throw new ApiError(0, null);

  let data = null;
  if (res.status !== 204) {
    try { data = await res.json(); } catch { data = null; }
  }
  if (!res.ok) throw new ApiError(res.status, data);
  return data;
}

const ACCOUNTS = "/auth/admin/users";
const EDITS = "/auth/admin/authority/edits";
const APPLICATIONS = "/auth/admin/applications";

const one = (id) => "/" + encodeURIComponent(id);

const api = {
  // Every person's account, and what may be done to one.
  users: {
    list: () => call("GET", ACCOUNTS).then((r) => (r && r.data) || []),
    create: (body) => call("POST", ACCOUNTS, body || {}),
    update: (id, body) => call("PATCH", ACCOUNTS + one(id), body || {}),
    remove: (id) => call("DELETE", ACCOUNTS + one(id)),
    setPassword: (id, password) => call("POST", ACCOUNTS + one(id) + "/password", { password }),
  },
  // Where somebody else is signed in, and ending it.
  sessions: {
    list: (id) => call("GET", ACCOUNTS + one(id) + "/sessions").then((r) => (r && r.data) || []),
    revokeOne: (id, sid) => call("POST", ACCOUNTS + one(id) + "/sessions" + one(sid) + "/revoke", {}),
    revokeAll: (id) => call("POST", ACCOUNTS + one(id) + "/sessions/revoke-all", {}),
  },
  // Who may do what: read, one change against the version on screen, and the rules' verdict on edits
  // nobody has made.
  authority: {
    read: () => call("GET", "/auth/admin/authority"),
    edit: (version, edit) => call("POST", EDITS, { ...edit, version }),
    check: (edits) => call("POST", "/auth/admin/authority/checks", { edits }).then((r) => (r && r.results) || []),
  },
  // The caller's own actions here, and which action each gated request needs.
  me: () => call("GET", "/me/access"),
  operations: () => call("GET", "/auth/admin/operations"),
  // The applications this provider signs people in to.
  applications: {
    list: () => call("GET", APPLICATIONS).then((r) => (r && r.data) || []),
    add: (body) => call("POST", APPLICATIONS, body),
    set: (id, body) => call("PATCH", APPLICATIONS + one(id), body),
    remove: (id) => call("DELETE", APPLICATIONS + one(id)),
    addClient: (id, body) => call("POST", APPLICATIONS + one(id) + "/clients", body),
    removeClient: (id, clientId) => call("DELETE", APPLICATIONS + one(id) + "/clients" + one(clientId)),
    rotateSecret: (id, clientId) => call("POST", APPLICATIONS + one(id) + "/clients" + one(clientId) + "/secret"),
  },
};

export { ACCOUNTS, APPLICATIONS, EDITS, api };
