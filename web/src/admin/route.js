// route.js — where the admin pages are, in the address's fragment: `#/<page>[/<id>][?<name>=<value>&…]`.
//
// The fragment is the source of truth, so a link opens a page with its parameters filled in
// (`#/accounts?status=pending`), and Back and Forward move between pages. An empty or unknown address is
// the overview. A page's `id` is URI-decoded.

import React from "react";

const PAGES = ["overview", "accounts", "roles", "permissions", "catalog", "assignments", "applications"];

function parse(hash) {
  const raw = String(hash || "").replace(/^#\/?/, "");
  const [path, query] = raw.split("?");
  const [name, ...rest] = path.split("/");
  const page = PAGES.includes(name) ? name : "overview";
  let id = null;
  if (PAGES.includes(name) && rest.length) {
    try { id = decodeURIComponent(rest.join("/")) || null; } catch { id = null; }
  }
  return { page, id, params: Object.fromEntries(new URLSearchParams(query || "")) };
}

function hashOf(page, id, params) {
  const query = new URLSearchParams(Object.entries(params || {}).filter(([, v]) => v != null && v !== "")).toString();
  return "#/" + page + (id ? "/" + encodeURIComponent(id) : "") + (query ? "?" + query : "");
}

function useRoute() {
  const [route, setRoute] = React.useState(() => parse(window.location.hash));
  React.useEffect(() => {
    const changed = () => setRoute(parse(window.location.hash));
    window.addEventListener("hashchange", changed);
    return () => window.removeEventListener("hashchange", changed);
  }, []);
  return route;
}

function go(page, id, params) {
  window.location.hash = hashOf(page, id, params);
}

export { PAGES, go, hashOf, useRoute };
