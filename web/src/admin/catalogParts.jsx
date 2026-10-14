// catalogParts — what the Catalog and the permission page share: which application an action belongs
// to, and that application by name.

import React from "react";

import { useStore } from "@thekrystalship/krystal";
import { applicationsStore, refreshApplications } from "./applicationsStore.js";
import { APPLICATIONS } from "./client.js";
import { componentOf } from "./kit.jsx";
import { accessStore, callRefusal } from "./stores.js";

const APPLICATION = "application:";

// The application an action belongs to: the registered application whose manifest declares it, this
// provider for its own, and KGSM for what a cluster's members declare.
function applicationOf(action) {
  const declarer = (action.declaredBy || []).find((d) => d.member.startsWith(APPLICATION));
  if (declarer) return declarer.member.slice(APPLICATION.length);
  return componentOf(action.action) === "auth" ? "auth" : "kgsm";
}

// An application id by the name a person reads. The registered applications are read when the
// provider offers them to the caller; until then an application is named by its id.
function useApplicationNames() {
  useStore(accessStore, (s) => s.report);
  const list = useStore(applicationsStore, (s) => s.list);
  const allowed = !callRefusal("GET", APPLICATIONS);
  React.useEffect(() => { if (allowed) refreshApplications(); }, [allowed]);
  return React.useCallback((id) => {
    if (id === "auth") return "tks-auth";
    const a = (list || []).find((x) => x.id === id);
    return a ? a.name : id === "kgsm" ? "KGSM" : id;
  }, [list]);
}

// Applications in the order a catalog lists them: this provider's, KGSM's, then the registered ones by name.
const applicationRank = (id) => (id === "auth" ? 0 : id === "kgsm" ? 1 : 2);

export { applicationOf, applicationRank, useApplicationNames };
