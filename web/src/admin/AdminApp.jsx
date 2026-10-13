// AdminApp — administering who may do what here: accounts and their approval, roles, permissions, the
// catalog of declared actions, assignments, service accounts' requests, and the applications this
// provider signs people in to.
//
// The pages stand in the sidebar frame, one per place, each reached at `#/<page>` (`route.js`). A place
// is offered when the request behind it may be made; the pages gate their own controls the same way.

import React from "react";

import { EmptyState, PageHeader, useStore } from "@thekrystalship/krystal-ui";
import { Frame } from "../chrome/Frame.jsx";
import { placeOf } from "../chrome/nav.js";
import { AccountPage } from "./AccountPage.jsx";
import { AccountsPage } from "./AccountsPage.jsx";
import { ApplicationsAdmin } from "./ApplicationsAdmin.jsx";
import { AssignmentsPage } from "./AssignmentsPage.jsx";
import { CatalogAdmin } from "./CatalogAdmin.jsx";
import { APPLICATIONS } from "./client.js";
import { OverviewPage } from "./OverviewPage.jsx";
import { PermissionsAdmin } from "./PermissionsAdmin.jsx";
import { RolePage } from "./RolePage.jsx";
import { RolesPage } from "./RolesPage.jsx";
import { go, useRoute } from "./route.js";
import { ServiceRequests } from "./ServiceRequests.jsx";
import { profile, signOut } from "./session.js";
import { accessStore, administers, authorityStore, callRefusal } from "./stores.js";

const PAGES = {
  overview: () => <OverviewPage />,
  accounts: (route) => (route.id ? <AccountPage key={route.id} id={route.id} /> : <AccountsPage params={route.params} />),
  roles: (route) => (route.id ? <RolePage key={route.id} id={route.id} /> : <RolesPage />),
  permissions: () => <PermissionsAdmin />,
  catalog: () => <CatalogAdmin />,
  assignments: (route) => <AssignmentsPage params={route.params} />,
  services: () => <ServiceRequests />,
  applications: () => <ApplicationsAdmin />,
};

// The pages that draw their own `PageHeader`, because they carry actions or a title of their own.
const OWN_HEADER = new Set(["accounts", "roles"]);

// What is waiting on somebody, counted from the authority: accounts awaiting approval, services'
// requirements awaiting a decision, and engine actions no permission holds.
function badgesOf(view) {
  if (!view) return {};
  const pending = view.accounts.filter((a) => a.status === "pending").length;
  const waiting = view.accounts.reduce((n, a) => n + (a.requirements || []).filter((q) => q.state === "waiting").length, 0);
  const unmapped = view.catalog.filter((a) => a.unmapped).length;
  return {
    accounts: { count: pending, tone: "warn" },
    services: { count: waiting, tone: "warn" },
    catalog: { count: unmapped, tone: "info" },
  };
}

function AdminApp() {
  const access = useStore(accessStore);
  const view = useStore(authorityStore, (s) => s.view);
  const route = useRoute();
  const who = profile();

  const allowed = access.state === "ok" && administers();
  React.useEffect(() => { if (allowed) authorityStore.refresh().catch(() => {}); }, [allowed]);

  const offeredApplications = allowed && !callRefusal("GET", APPLICATIONS);
  const page = route.page === "applications" && !offeredApplications ? "overview" : route.page;
  const place = placeOf(page);

  let body;
  if (access.state === "loading") body = <EmptyState icon="loader" title="Loading…" />;
  else if (access.state === "unavailable") body = <EmptyState icon="database" tone="warn" title="The account store could not be read" />;
  else if (access.state !== "ok") body = <EmptyState icon="wifi-off" tone="warn" title="Couldn’t reach the provider" />;
  else if (!allowed) body = <EmptyState icon="lock" title="You don’t have access to this" />;
  else {
    body = (
      <>
        {!OWN_HEADER.has(page) && <PageHeader title={place.label} />}
        {PAGES[page](route)}
      </>
    );
  }

  const user = who ? { name: who.preferred_username || who.sub, display: who.name || who.preferred_username || who.sub } : null;

  return (
    <Frame
      doc="admin"
      active={page}
      showAdmin={allowed}
      offered={offeredApplications ? ["applications"] : []}
      badges={badgesOf(allowed ? view : null)}
      user={user}
      onSignOut={() => signOut()}
      onNavigate={(p) => go(p.id)}>
      <div className="admin-body">{body}</div>
    </Frame>
  );
}

export { AdminApp };
