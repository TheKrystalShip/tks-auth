import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { siteIdentity } from "@thekrystalship/krystal/vite";
import identity from "./identity.js";
import { fileURLToPath } from "node:url";

// tks-auth's pages, built under `base: "/ui/"` because the daemon serves every asset of them there
// and nothing else of its origin belongs to this bundle.
//
// One document per page, each carrying its own floor: the daemon serves the one a request calls for
// (`ProviderBundle`), reading it from disk on every request. The sign-in, wait, account and device
// documents share one application that replaces the floor on mount; the admin document is its own application,
// an OpenID Connect client of the provider that served it. The design system gives every document its
// theme before first paint, its icon in that theme, and the splash (`siteIdentity`): all of it files
// under the base, as the provider's policy allows no inline script or style.
const page = (name) => fileURLToPath(new URL(`./${name}.html`, import.meta.url));

export default defineConfig({
  base: "/ui/",
  plugins: [react(), siteIdentity(identity)],
  server: { port: 5175 },
  test: { environment: "jsdom", include: ["src/**/*.test.{js,jsx}"] },
  build: {
    outDir: "dist",
    target: "es2022",
    sourcemap: true,
    rollupOptions: {
      input: {
        "auth-sign-in": page("auth-sign-in"),
        "auth-wait": page("auth-wait"),
        "auth-account": page("auth-account"),
        "auth-device": page("auth-device"),
        admin: page("admin"),
      },
    },
  },
});
