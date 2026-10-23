// Frame — the sidebar frame both documents stand in: the brand, the places of `nav.js` in labelled
// groups, the signed-in person at the foot (their menu: Profile, Settings, which is Appearance, and Sign
// out), and on a phone a drawer opened by the edge handle or a swipe.
//
// A place in this document is handed to `onNavigate`; a place in the other one is a page load at its
// address. The collapsed state is kept for the browser, so both documents open the way it was left.
//
// The caller decides what to offer and what to count: `showAdmin` shows the administration groups,
// `offered` names the gated places that may be shown, and `badges` maps a place id to `{ count, tone }`.
// Nothing here knows how any of it is decided.

import React from "react";

import {
  AppShell, NavItem, Sidebar, SidebarAccount, SidebarGroup, useEdgeSwipe, useStoredFlag,
} from "@thekrystalship/krystal";
import { GROUPS, PLACES, hrefOf, placeOf } from "./nav.js";
import "./frame.css";

const MARK = import.meta.env.BASE_URL + "assets/tks-mark.png";

function Frame({ doc, active, showAdmin, offered, badges, user, onSignOut, onNavigate, children }) {
  const [collapsed, setCollapsed] = useStoredFlag("tks-auth:sidebar:collapsed");
  const [drawerOpen, setDrawerOpen] = React.useState(false);
  useEdgeSwipe(drawerOpen, setDrawerOpen, false, undefined);

  const open = (place) => {
    setDrawerOpen(false);
    if (place.doc === doc) onNavigate(place);
    else window.location.assign(hrefOf(place));
  };

  const shown = (place) => {
    if (place.doc !== "account" && !showAdmin) return false;
    return !place.gated || (offered || []).includes(place.id);
  };

  const groups = GROUPS
    .map((g) => ({ ...g, places: PLACES.filter((p) => p.group === g.id && shown(p)) }))
    .filter((g) => g.places.length > 0);

  const sidebar = (
    <Sidebar
      mark={MARK}
      wordmark="Auth"
      open={drawerOpen}
      collapsed={collapsed}
      onToggleCollapse={() => setCollapsed((c) => !c)}
      foot={user && (
        <SidebarAccount
          user={user}
          collapsed={collapsed}
          onProfile={() => open(placeOf("profile"))}
          onSettings={() => open(placeOf("appearance"))}
          onSignOut={onSignOut} />
      )}>
      {groups.map((g) => (
        <SidebarGroup key={g.id} label={g.label}>
          {g.places.map((p) => {
            const badge = (badges && badges[p.id]) || {};
            return (
              <div key={p.id} data-place={p.id}>
                <NavItem
                  icon={p.icon}
                  label={p.label}
                  active={p.doc === doc && p.id === active}
                  badge={badge.count || 0}
                  tone={badge.tone}
                  onClick={() => open(p)} />
              </div>
            );
          })}
        </SidebarGroup>
      ))}
    </Sidebar>
  );

  return (
    <AppShell sidebar={sidebar} collapsed={collapsed} drawerOpen={drawerOpen} onDrawer={setDrawerOpen}>
      {children}
    </AppShell>
  );
}

export { Frame };
