// route.js — the account document's addresses: `#/<page>` for the four pages, and the fragments a
// round trip through a provider, or another surface, leaves on the address.
//
// The pages are `profile` (also the empty address), `sign-in`, `devices` and `appearance`. The
// fragments (`#linked=`, `#link_error=`, `#proved`, `#confirm`, `#reauth_error=`) are read once by
// `takeOutcome`, which clears them from the address.

import React from "react";
import { providerLabel } from "@thekrystalship/krystal";

const PAGES = ["profile", "sign-in", "devices", "appearance"];

function pageOf(hash) {
  const id = hash.replace(/^#\/?/, "").split("?")[0];
  return PAGES.includes(id) ? id : "profile";
}

function useRoute() {
  const [page, setPage] = React.useState(() => pageOf(window.location.hash));
  React.useEffect(() => {
    const onChange = () => setPage(pageOf(window.location.hash));
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return page;
}

function go(page) {
  window.location.hash = "#/" + page;
}

// What a returning round trip reported, read once from the fragment and then cleared from the address.
// A link, an unlink or a proof lands on the sign-in methods; `#confirm` stays where the person is.
function takeOutcome() {
  const hash = window.location.hash.replace(/^#/, "");
  if (!hash || hash.startsWith("/")) return null;
  const params = new URLSearchParams(hash);
  const settle = (outcome, page) => {
    history.replaceState(null, "", window.location.pathname + (page ? "#/" + page : ""));
    return outcome;
  };
  if (params.has("linked")) return settle({ tone: "ok", text: providerLabel(params.get("linked")) + " connected." }, "sign-in");
  if (params.has("proved")) return settle({ tone: "proved" }, "sign-in");
  if (params.has("confirm")) return settle({ tone: "confirm" }, null);
  const linkError = params.get("link_error");
  if (linkError === "identity_taken") return settle({ tone: "bad", text: "That account is already connected to someone else." }, "sign-in");
  if (linkError) return settle({ tone: "bad", text: "Connecting didn’t complete." }, "sign-in");
  const reauthError = params.get("reauth_error");
  if (reauthError === "other_account") return settle({ tone: "bad", text: "That account isn’t connected to this one." }, "sign-in");
  if (reauthError) return settle({ tone: "bad", text: "Confirming didn’t complete." }, "sign-in");
  return settle(null, null);
}

export { PAGES, go, takeOutcome, useRoute };
