# `web/` — tks-auth's pages

A Vite + React 18 app on `@thekrystalship/krystal`, built under `base: "/ui/"` into `dist/`, which
the release and `deploy/deploy.sh` place in `ui/` beside the binary — where the settings file's
`UiPath` points. The daemon reads each document from disk per request (`ProviderBundle`), so new pages
are live the moment the files land.

```bash
npm ci && npm run build   # → dist/
npm run check             # every document within the provider's policy, the floor intact, no bearer on the sign-in pages
npm run lint              # 0 errors
```

## Two applications, five documents

- **`auth-sign-in.html`, `auth-wait.html`, `auth-account.html`, `auth-device.html` → `src/pages/`.**
  Signing in, the wait for approval, the account page, and signing a device in with the code it shows.
  Every call is same-origin and authenticated by the provider's cookie; nothing these pages hold can call
  an application, so they may import nothing from `src/admin/` (`npm run check` walks the import graph).
  Each document carries its own **floor** — a sign-in form that posts without script, the providers'
  marker the daemon fills with links carrying Krystal's button classes, a wait that refreshes only
  under `<noscript>`, a code form the provider answers with the device and its two buttons — and the
  application removes it on mount.
  - The account document is four pages in the frame: Profile, Sign-in methods, Devices & sessions and
    Appearance (`src/pages/account/`). `src/pages/route.js` maps `#/<page>` to them and reads, once, the
    fragments a round trip through a provider or another surface leaves (`#linked=`, `#link_error=`,
    `#proved`, `#confirm`, `#reauth_error=`).
- **`admin.html` → `src/admin/`.** Overview, accounts, roles, permissions, the catalog grouped by
  application, assignments, service requests and applications, in the frame. An OpenID Connect client of
  the provider that served it — the built-in public client `tks-auth`, its redirect `/admin/` on the
  issuer's own origin (`session.js`) — holding a bearer for tks-auth's own application, which the admin
  routes accept. The bearer is attached in `client.js` alone, which renews it once when it is refused
  (`eslint.config.js` enforces it). The daemon serves this document to administrators only and answers
  everybody else as it answers an unknown path.
  - Addresses are `#/<page>[/<id>][?<name>=<value>]` (`src/admin/route.js`); an account, a role, a
    permission and an application each open on a page of their own at `#/<page>/<id>`.
  - It installs as the web app "Auth" from `public/admin.webmanifest` (scope: the whole origin, so
    sign-in and the account pages stay in the app) and the iOS tags in its head, with no service worker:
    nothing behind an administrator's bearer is kept offline.
- **The frame** (`src/chrome/Frame.jsx`, places in `src/chrome/nav.js`) is Krystal's `AppShell` and
  `Sidebar`, shared by the account and admin documents. It imports nothing from `src/admin/`; the
  Administration groups appear when the caller says the account administers, and moving between the two
  documents is a plain link.

**These pages name no action.** A control is gated on the request it would make, against the
provider's published operations and the caller's `/me/access` (`src/admin/stores.js`, over
`src/lib/operations.js` and `src/lib/access.js`), and a control the rules would refuse stays on screen
with the provider's reason beside it (`authorityStore.check`). The page holds no copy of the rules.

**A page's parameters are in its address** (`src/admin/route.js`): `#/assignments?scope=…&label=…` is
how another surface — a KGSM server's "manage access" — opens the roles held at one scope. A scope
this provider holds no record of is named by the `label` the link carries.

## Policy

Every document is served under the daemon's content security policy: no inline script, no inline
style, `connect-src 'self'`, no framing. A `style` attribute in a document, or a `<style>` element, is
refused by the browser with nothing but a console line; `npm run check` fails the build on either.
React's `style={…}` props are set through the CSSOM and are not affected.

## The stylesheet

`src/styles/kit.css` imports the sheets of `@thekrystalship/krystal` that these pages draw with. The package's
sheets sit in the cascade layers `krystal.tokens`, `krystal.base` and `krystal.components`; the document
resets of `src/styles/base.css` sit in `tks.reset`, ordered before them, and every other rule is
unlayered, so it wins over the package without raised specificity. The other local CSS is
`src/admin/admin.css` and `src/pages/pages.css`: only what is tks-auth's own, written with tokens and no
literal colour. Every control, field, list, pill, dialog and card is Krystal's, and its class names are the
package's public interface, so a rule here targets a package class only to place it. `npm run lint` extends
`@thekrystalship/krystal/eslint` and `npm run lint:css` runs Stylelint with
`@thekrystalship/krystal/stylelint`.

Every form is Krystal's form layer: `useForm` holds the values, rules and refusals, and `Form` or
Krystal's `FormDialog` (a dialog that is itself the form) draws its `FormField`s. The
admin pages' `useRefusingForm` (`src/admin/kit.jsx`) puts the provider's refusal beside the fields as a
`RefusalNote`. Every question before a change is Krystal's `ConfirmDialog`, which shows a `RefusalNote` as its refusal; the sign-in page is its
`SignInScreen`, and the admin lists and account page are `ListPage` and `DetailPage`. `eslint.config.js`
merges the package's `restrictedSyntax` and `restrictedImports` into its own rules at error.

`public/theme-boot.js` applies the stored theme before first paint (the policy forbids inline script).
Its theme lists mirror Krystal's `THEME_OPTS`; `npm run check` fails when they drift.

## Harness

`scripts/visual-harness/` covers these pages: `oidc-pages.mjs` (sign-in, wait, account, device),
`oidc-provider.mjs`, `p5-access.mjs`, `p5-applications.mjs`, `auth-frame.mjs` and `auth-admin.mjs`.
