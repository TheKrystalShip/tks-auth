// OverviewPage — what waits on somebody: accounts awaiting approval, services' requirements awaiting a
// decision, engine actions no permission holds, and the applications this provider signs people in to.
//
// A card is drawn when the person may read what it summarises (`callRefusal`, the provider's own
// answer), and the first three only while something is waiting; with nothing waiting the page says so.

import React from "react";

import { BriefCard, EmptyState } from "@thekrystalship/krystal-ui";
import { ACCOUNTS, APPLICATIONS, api } from "./client.js";
import { authorityGate, useAuthority } from "./kit.jsx";
import { go } from "./route.js";
import { callRefusal } from "./stores.js";

const SHOWN = 5;

function Card({ id, icon, title, count, tone, page, items }) {
  return (
    <div data-card={id} data-count={count}>
      <BriefCard icon={icon} title={title} count={count} countTone={tone} onViewAll={() => go(page)}>
        {items.length > 0 && (
          <ul className="overview-list">
            {items.slice(0, SHOWN).map((t) => <li key={t}>{t}</li>)}
          </ul>
        )}
      </BriefCard>
    </div>
  );
}

function OverviewPage() {
  const state = useAuthority();
  const readApplications = !callRefusal("GET", APPLICATIONS);
  const readAccounts = !callRefusal("GET", ACCOUNTS);
  const [applications, setApplications] = React.useState(null);

  React.useEffect(() => {
    if (!readApplications) return undefined;
    let cancelled = false;
    api.applications.list().then((l) => { if (!cancelled) setApplications(l); }, () => {});
    return () => { cancelled = true; };
  }, [readApplications]);

  const gate = authorityGate(state);
  if (gate) return gate;

  const view = state.view;
  const pending = view.accounts.filter((a) => a.status === "pending");
  const waiting = view.accounts.filter((a) => (a.requirements || []).some((q) => q.state === "waiting"));
  const waitingCount = waiting.reduce((n, a) => n + a.requirements.filter((q) => q.state === "waiting").length, 0);
  const unmapped = view.catalog.filter((a) => a.unmapped);

  const attention = [
    readAccounts && pending.length > 0 && (
      <Card key="accounts" id="accounts" icon="user-check" title="Waiting for approval" count={pending.length} tone="warn"
        page="accounts" items={pending.map((a) => a.displayName || a.username)} />
    ),
    waitingCount > 0 && (
      <Card key="services" id="services" icon="bot" title="Service requests" count={waitingCount} tone="warn"
        page="services" items={waiting.map((a) => a.username)} />
    ),
    unmapped.length > 0 && (
      <Card key="catalog" id="catalog" icon="list-tree" title="Unmapped actions" count={unmapped.length} tone="info"
        page="catalog" items={unmapped.map((a) => a.action)} />
    ),
  ].filter(Boolean);

  return (
    <>
      {attention.length === 0 && <EmptyState icon="check-circle-2" title="Nothing needs attention" />}
      <div className="overview-grid">
        {attention}
        {readApplications && applications && (
          <Card id="applications" icon="app-window" title="Applications" count={applications.length} tone="neutral"
            page="applications" items={applications.map((a) => a.name || a.id)} />
        )}
      </div>
    </>
  );
}

export { OverviewPage };
