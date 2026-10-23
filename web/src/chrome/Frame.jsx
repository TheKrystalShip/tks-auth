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
//
// Every page ends with the footer: what Auth is, the Android app on an Android phone's browser, and
// the source. The app is the APK this daemon serves beside the pages, at `/ui/auth.apk`, which the
// phone installs once it allows installing from the browser; inside the app, whose shell puts
// `TksShell` on the page, it is not offered.

import React from "react";

import {
  AppShell, Footer, GithubMark, NavItem, Sidebar, SidebarAccount, SidebarGroup, useEdgeSwipe, useStoredFlag,
} from "@thekrystalship/krystal";
import { GROUPS, PLACES, hrefOf, placeOf } from "./nav.js";
import "./frame.css";

const MARK = import.meta.env.BASE_URL + "assets/tks-mark.png";
const ORG = "https://github.com/TheKrystalShip";

const APP = [
  { name: "Auth for Android", href: import.meta.env.BASE_URL + "auth.apk", icon: "smartphone", featured: true, sub: "Your account, and signing in a device, as an app" },
];
const offerApp = () => typeof navigator !== "undefined" && /Android/.test(navigator.userAgent) && !window.TksShell;

const SOURCE = [
  { name: "TheKrystalShip", href: ORG, glyph: <GithubMark size={18} />, featured: true, sub: "Where everything here is built" },
  { name: "tks-auth", href: ORG + "/tks-auth", icon: "git-branch", sub: "The accounts, and signing in to every site" },
  { name: "tks-krystal", href: ORG + "/tks-krystal", icon: "git-branch", sub: "The design system these pages are drawn with" },
];

function FrameFooter() {
  return (
    <Footer
      mark={MARK}
      wordmark="Auth"
      tagline="One account for every site of The Krystal Ship: sign in once, and every site knows who you are and what you may do."
      note="Self-hosted and open source"
      groups={[
        ...(offerApp() ? [{ label: "App", links: APP }] : []),
        { label: "Source code", mono: true, links: SOURCE },
      ]}
      copy={`© ${new Date().getFullYear()} The Krystal Ship`}
      end={
        <a href={ORG} target="_blank" rel="noreferrer noopener" className="frame-footer-org">
          <GithubMark size={13} />
          github.com/TheKrystalShip
        </a>
      }
    />
  );
}

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
      <FrameFooter />
    </AppShell>
  );
}

export { Frame };
