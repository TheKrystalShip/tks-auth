// CatalogPage — every action declared to this provider, grouped by the application it belongs to:
// tks-auth's own, KGSM's (each component a group of its own) and each registered application's, with
// its effect, the scope kind it is granted at, and who declares it at which version.
//
// *Unmapped* is an action filed into no permission: nobody but an Owner can perform it until it is. The
// application and unmapped filters are in the address (`#/catalog?application=…&unmapped=1`), so a link
// opens the queue, and an unmapped action is filed into a permission from its own row.

import React from "react";

import {
  EmptyState, List, ListRow, PageHeader, Pill, Select, SettingsSection, Toolbar, ToolbarCount, ToolbarFilters,
  ToolbarSearch, useFilters,
} from "@thekrystalship/krystal";
import { applicationOf, applicationRank, useApplicationNames } from "./catalogParts.jsx";
import { RefusalNote, authorityGate, componentOf, useAuthority } from "./kit.jsx";
import { go } from "./route.js";
import { authorityStore, editRefusal } from "./stores.js";

const EFFECT_TONE = { read: "ok", write: "danger", execute: "warn" };

function CatalogPage({ params }) {
  const state = useAuthority();
  const view = state.view;
  const applicationName = useApplicationNames();
  const canFile = !editRefusal("permission.actions");
  const { query, setQuery, debouncedQuery } = useFilters({ search: "" });
  const [refusal, setRefusal] = React.useState(null);
  const [busy, setBusy] = React.useState(false);

  const catalog = React.useMemo(() => (view ? view.catalog : []), [view]);
  const applicationIds = [...new Set(catalog.map(applicationOf))]
    .sort((x, y) => applicationRank(x) - applicationRank(y) || applicationName(x).localeCompare(applicationName(y)));
  const application = applicationIds.includes(params.application) ? params.application : "all";
  const onlyUnmapped = params.unmapped === "1";
  const needle = debouncedQuery.trim().toLowerCase();

  const groups = React.useMemo(() => {
    const by = {};
    catalog
      .filter((a) => !onlyUnmapped || a.unmapped)
      .filter((a) => application === "all" || applicationOf(a) === application)
      .filter((a) => !needle || a.action.toLowerCase().includes(needle) || (a.title || "").toLowerCase().includes(needle))
      .forEach((a) => {
        const app = applicationOf(a);
        const component = componentOf(a.action);
        ((by[app] = by[app] || {})[component] = by[app][component] || []).push(a);
      });
    return Object.keys(by)
      .sort((x, y) => applicationRank(x) - applicationRank(y) || applicationName(x).localeCompare(applicationName(y)))
      .map((app) => ({
        app, name: applicationName(app),
        count: Object.values(by[app]).reduce((n, list) => n + list.length, 0),
        components: Object.keys(by[app]).sort().map((c) => ({
          component: c, actions: by[app][c].sort((a, b) => a.action.localeCompare(b.action)),
        })),
      }));
  }, [catalog, onlyUnmapped, application, needle, applicationName]);

  const gate = authorityGate(state);
  if (gate) return gate;

  const unmapped = catalog.filter((a) => a.unmapped).length;
  const shown = groups.reduce((n, g) => n + g.count, 0);
  const address = (next) => go("catalog", null, { application: application === "all" ? null : application, unmapped: onlyUnmapped ? "1" : null, ...next });

  const fileInto = async (action, permissionId) => {
    const p = view.permissions.find((x) => x.id === permissionId);
    if (!p) return;
    setBusy(true);
    setRefusal(null);
    const r = await authorityStore.edit({ kind: "permission.actions", permissionId, actions: [...p.actions, action] });
    setBusy(false);
    if (!r.ok) setRefusal(r.refusal);
  };

  return (
    <>
      <PageHeader title="Catalog" />
      <RefusalNote refusal={refusal} />

      <Toolbar>
        <ToolbarSearch value={query} onChange={setQuery} placeholder="Search actions" />
        <ToolbarFilters fields={[
          {
            id: "application", label: "Application", value: application, default: "all",
            options: [
              { value: "all", label: "All", count: catalog.length },
              ...applicationIds.map((id) => ({
                value: id, label: applicationName(id), count: catalog.filter((a) => applicationOf(a) === id).length,
              })),
            ],
            onChange: (v) => address({ application: v === "all" ? null : v }),
          },
          {
            id: "unmapped", label: "Filing", value: onlyUnmapped ? "1" : "all", default: "all",
            options: [
              { value: "all", label: "All", count: catalog.length },
              { value: "1", label: "Unmapped", count: unmapped },
            ],
            onChange: (v) => address({ unmapped: v === "1" ? "1" : null }),
          },
        ]} />
        <ToolbarCount shown={shown} total={catalog.length} unit={catalog.length === 1 ? "action" : "actions"} />
      </Toolbar>

      {groups.length === 0 && (
        <EmptyState icon="list-checks"
          title={catalog.length === 0 ? "Nothing declares any action" : onlyUnmapped && !needle && application === "all" ? "Every action is filed" : "No action matches"} />
      )}
      {groups.map((g) => (
        <div key={g.app} data-application={g.app}>
          <SettingsSection icon="app-window" title={g.name} meta={g.count + (g.count === 1 ? " action" : " actions")}>
            {g.components.map((c) => (
              <React.Fragment key={c.component}>
                {(g.components.length > 1 || c.component !== g.app) && <div className="admin-group-title">{c.component}</div>}
                <List label={g.name + " " + c.component + " actions"} className="admin-flat-list">
                  {c.actions.map((a) => (
                    <ListRow key={a.action} data-action={a.action}
                      title={a.title}
                      sub={a.action}
                      meta={<>
                        <Pill size="sm" tone={EFFECT_TONE[a.effect]}>{a.effect}</Pill>
                        <Pill size="sm">{a.self ? "self" : a.scope}</Pill>
                        {a.unmapped && !(canFile && view.permissions.length > 0) && <Pill size="sm" tone="warn">unmapped</Pill>}
                      </>}
                      aside={a.declaredBy.map((d) => d.member + (d.version ? " " + d.version : "")).join(", ") || "—"}
                      actions={a.unmapped && canFile && view.permissions.length > 0 ? (
                        <Select value="" aria-label={"File " + a.action + " into a permission"} disabled={busy}
                          onChange={(e) => { if (e.target.value) fileInto(a.action, e.target.value); }}>
                          <option value="">File into…</option>
                          {view.permissions.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                        </Select>
                      ) : null} />
                  ))}
                </List>
              </React.Fragment>
            ))}
          </SettingsSection>
        </div>
      ))}
    </>
  );
}

export { CatalogPage };
