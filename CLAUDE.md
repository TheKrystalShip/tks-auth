# CLAUDE.md — tks-auth

`tks-auth` is the organization's sign-in provider: an OpenID Connect daemon holding the accounts, the
registered applications and each application's roles, and minting every session. A change here changes who
can do what on every surface at once. Canon: `../kgsm-docs/components/tks-auth.md`; the model and the doors
are in `README.md`.

## Layout

- `src/Auth` (`TheKrystalShip.Auth`): identity, actor convention, claim names. `src/Auth.Access`: the
  access model and `AccessEvaluator`. `src/Auth.Users`: the account store. `src/Auth.Journal`: the audit
  events.
- `src/Auth.Server`: the `tks-auth` daemon, built from the libraries by project reference. Nothing here is
  published to a feed. Its minter is `src/Auth.Server/Minting/`.
- `client/`: `@thekrystalship/auth-client`, the browser sign-in package every site configures (own
  `package.json`, Vitest over a fake provider; `client/README.md`).
- `web/`: the sign-in, wait, account, device and admin pages (Vite + React on krystal-ui), built into `ui/`
  beside the binary.
- **Each project's locked decisions live in a `CLAUDE.md` beside it.** Read the one for the project you are
  changing; the tests next to it are the specification, so extend them before the code.

## Rules that span the repo

- **`TheKrystalShip.Auth` has ZERO package dependencies and stays AOT-safe.** No `HttpClient`,
  configuration binder, ORM or logging abstraction; `IsAotCompatible` and the trim analyzer stay green. I/O
  belongs in a sibling project.
- **A subject is unique only within its provider.** `AccountIdentity` carries both and `Handle` is
  `provider:subject`. Never key a user, a session or a link on the subject alone, and never let `Handle`
  fall back to the username.
- **`ActorString` and `Handle` are different strings on purpose:** the handle keys things, the actor string
  (`provider:username`) is what a human reads in an audit log.
- **One evaluator decides access.** `AccessEvaluator` answers every "may this account do this action" and
  computes the actions an application finds in its token. Do not add a second check beside it.
- **Every application has its own audience, and a token for one is refused at every other.** Nothing here
  accepts one application's token where another's is expected.
- **No surface derives access from an outside group, a guild or a provider's role.** The account store is
  the single authority on what anyone may do.
- **Parsing is fail-closed.** An unrecognised status is disabled, an unrecognised action id is malformed,
  and nothing unrecognised ever grants.
- **A provider is a key, never a property.** The daemon reads `Auth:Providers:<name>:ClientId` and
  `ClientSecret`; wiring a new provider is a pair of environment keys and no code. An unwired provider and
  an unknown one are one answer (not offered).
- **JSON is read and written through registered types** (`ServerJsonContext`, `AccessJsonContext`), never a
  JSON DOM.
- The daemon is `TheKrystalShip.Auth.Server`; its binary and unit are `tks-auth`.

## Commands

```bash
dotnet test tks-auth.slnx -c Release
dotnet publish src/Auth.Server/Server.csproj -c Release -r linux-x64 -o artifacts/publish   # 0 ILC warnings
(cd web && npm ci && npm run build && npm run check && npm run lint)   # the pages -> web/dist
(cd client && npm ci && npm test)
./deploy/deploy.sh   # both, the pages into /opt/tks-auth/ui beside the binary
./deploy/deploy-app.sh   # the Android app's release, as /opt/tks-auth/ui/auth.apk
```

The pages are checked in a real browser by the workspace harness (`scripts/visual-harness/`).

## Read before changing

- `../kgsm-docs/systems/identity/`: accounts, sign-in, sessions, applications.
- `../kgsm-docs/systems/authorization/`: the access model.
- `../kgsm-docs/reference/oidc-provider.md`, `session-tokens.md`, `action-manifest.md`,
  `operation-manifest.md`: the contracts.
- `README.md`, `web/CLAUDE.md`, and the `CLAUDE.md` beside each project.

## Version tracking

The daemon versions on its own clock, in `src/Auth.Server/Server.csproj`; `deploy/version.sh` reads it,
because that is the one the pacman package ships. The browser client versions in `client/package.json`.

**Tags carry the prefix of the thing they version:** a bare `v*` for the daemon and `client-v*` for the
browser client. Only the bare `v*` fires the release workflow, which asserts the tag against
`deploy/version.sh`.

- Bump on any user-facing change; patch for fixes, minor for additions, major for a breaking change, with a
  `CHANGELOG.md` entry under `## [Unreleased]`.
- The browser client (`@thekrystalship/auth-client`) is published to GitHub Packages with `npm publish` from
  `client/`.
