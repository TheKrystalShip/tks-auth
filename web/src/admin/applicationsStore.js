// applicationsStore — the applications this provider signs people in to, read when a page wanting them
// mounts, and the client secrets a change has just made.
//
// A secret is made by the provider and shown in the answer that made it, never again: `issuedStore` holds
// it in memory only, for the application page that shows it, and the page empties it when it is dismissed
// or left.

import React from "react";

import { useStore } from "@thekrystalship/krystal-ui";
import { createStore } from "@thekrystalship/krystal-ui/lib/store";
import { APPLICATIONS, api } from "./client.js";
import { accessStore, callRefusal } from "./stores.js";

const applicationsStore = createStore({ status: "idle", list: null, error: null });

// `{ applicationId, secrets: [{ clientId, secret }] }`, or null.
const issuedStore = createStore(null);

function refreshApplications() {
  return api.applications.list().then(
    (list) => applicationsStore.setState({ status: "ready", list, error: null }),
    (error) => applicationsStore.setState((s) => ({ ...s, status: "error", error })));
}

// The answer to a change that may have made secrets: hold them for the application they belong to.
function keepSecrets(applicationId, answer) {
  if (answer && answer.secrets && answer.secrets.length) issuedStore.setState({ applicationId, secrets: answer.secrets });
}

// The applications, whether the provider offers them to the caller, and the state of the read.
function useApplications() {
  useStore(accessStore, (s) => s.report);
  const { status, list, error } = useStore(applicationsStore);
  const allowed = !callRefusal("GET", APPLICATIONS);
  React.useEffect(() => { if (allowed) refreshApplications(); }, [allowed]);
  return { allowed, status, list, error };
}

export { applicationsStore, issuedStore, keepSecrets, refreshApplications, useApplications };
