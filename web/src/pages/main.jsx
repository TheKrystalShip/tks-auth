import { useEffect } from "react";
import { createRoot } from "react-dom/client";
import { settleSplash } from "@thekrystalship/krystal";

import "../styles/kit.css";
import "./pages.css";
import "@thekrystalship/krystal/lib/theme";

import { AccountApp } from "./account/AccountApp.jsx";
import { DeviceApp } from "./DeviceApp.jsx";
import { SignInApp } from "./SignInApp.jsx";
import { WaitApp } from "./WaitApp.jsx";

// tks-auth's own pages: signing in, waiting for approval, the account page, and signing a device in.
//
// Each page is a document of its own, served by the provider, with its floor already in it — a working
// form, or a wait that polls without script. This application replaces that floor when it mounts, so
// a bundle that fails to load leaves the floor standing rather than an empty page.
//
// One appearance, shared by every page on this origin: the preference stored under Krystal's
// theme key, set on the Appearance page. The design system's boot script applies it before first paint
// (the document's policy allows no inline script); Krystal's theme store applies it again when this loads and, for
// `auto`, follows the system colour scheme as it changes. The provider is handed no theme name.

const PAGES = { "sign-in": SignInApp, wait: WaitApp, account: AccountApp, device: DeviceApp };
const Page = PAGES[document.body.dataset.page] || SignInApp;

const floor = document.getElementById("floor");
if (floor) floor.remove();

// The splash leaves after the first commit nothing on screen holds it through.
function Root() {
  useEffect(() => settleSplash());
  return <Page />;
}

createRoot(document.getElementById("root")).render(<Root />);
