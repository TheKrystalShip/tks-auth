import { createRoot } from "react-dom/client";

import "../styles/kit.css";
import "./admin.css";
import "@thekrystalship/krystal/lib/theme";

import { Gate } from "./Gate.jsx";
import { session } from "./auth.js";
import { refreshAccess, refreshMembers } from "./stores.js";

// tks-auth's admin pages. The document is served by the provider with a floor of its own; this
// application replaces it once the session is restored, and the gate leaves for the provider's sign-in
// when there is none.
//
// One appearance, shared with the provider's other pages: krystal-ui's stored theme preference,
// applied by `theme-boot.js` before first paint and followed live by its theme store.

// Coming back from the provider, the page goes on to where the person was headed.
const returnTo = await session.restore();
if (returnTo) window.history.replaceState(null, "", returnTo);

refreshAccess();
refreshMembers();
// What this person may do moves when somebody else changes it; coming back to the tab reads it again.
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") refreshAccess();
});

const floor = document.getElementById("floor");
if (floor) floor.remove();
createRoot(document.getElementById("root")).render(<Gate />);
