# CLAUDE.md — tks-auth

`tks-auth` is the organization's sign-in provider (an OpenID Connect daemon holding the accounts and minting
every session) and the home of the access model, shipped as libraries every other component compiles
against. A change here changes who can do what on every surface at once. Canon:
`../kgsm-docs/components/tks-auth.md`; the model and the anchor's doors are in `README.md`.

## Layout

- `src/Auth` (`TheKrystalShip.Auth`): identity, actor convention, claim names. `src/Auth.Access`: the access
  model and `AccessEvaluator`. `src/Auth.Users`: the account store. `src/Auth.Journal`: the account and
  authority events. `src/Auth.Cluster`: what a member or leaf does as a resource server. `Auth.Testing`:
  the anchor's minter compiled for tests.
- `src/Auth.Anchor`: the `tks-auth` daemon, built from the libraries by project reference. Published to no
  feed. Its minter is `src/Auth.Anchor/Minting/`.
- `web/`: the sign-in, wait, account, device and admin pages (Vite + React on krystal-ui), built into `ui/`
  beside the binary.
- **Each package's locked decisions live in a `CLAUDE.md` beside it.** Read the one for the package you are
  changing; the tests next to it are the specification, so extend them before the code.

## Rules that span the repo

- **The packages match the roles.** The anchor mints; every other component takes `Auth.Cluster`, which
  verifies and reads a session and cannot produce one. No production project references `Auth.Testing`.
- **tks-auth runs with no KGSM; KGSM is a module of it.** A core change must not reach for anything the
  module registers, and nothing in the core touches `/etc/kgsm` or `/var/lib/kgsm`
  (`src/Auth.Anchor/CLAUDE.md` holds the seam).
- **`TheKrystalShip.Auth` has ZERO package dependencies and stays AOT-safe.** Every surface takes it. No
  `HttpClient`, configuration binder, ORM or logging abstraction; `IsAotCompatible` and the trim analyzer
  stay green. I/O belongs in a sibling package that only its consumers take.
- **A subject is unique only within its provider.** `KgsmIdentity` carries both and `Handle` is
  `provider:subject`. Never key a user, a session or a link on the subject alone, and never let `Handle`
  fall back to the username.
- **`ActorString` and `Handle` are different strings on purpose:** the handle keys things, the actor string
  (`provider:username`) is what a human reads in an audit log.
- **One evaluator decides access.** `AccessEvaluator` answers every "may this account do this action here",
  on the anchor and on every member from its replica, and computes the actions an application outside KGSM
  finds in its token. Do not add a second check beside it.
- **Every application has its own audience, and a token for one is refused at every other.** Nothing here
  accepts one application's token where another's is expected.
- **No surface derives access from an outside group, a guild or a provider's role.** The account store is
  the single authority on what anyone may do, kgsm-bot's Discord callers included.
- **Parsing is fail-closed.** An unrecognised status is disabled, an unrecognised action id is malformed,
  and nothing unrecognised ever grants.
- **A provider is a key, never a property.** The anchor reads `KgsmAuth:Providers:<name>:ClientId` and
  `ClientSecret`; wiring a new provider is a pair of environment keys and no code. An unwired provider and an
  unknown one are one answer (not offered).
- Namespace `TheKrystalShip.Auth`, package id to match. The daemon is `TheKrystalShip.Auth.Anchor`; its
  binary and unit are `tks-auth`.

## Commands

```bash
TMPDIR=~/.cache KGSM_JOURNAL_STATE_ROOT=<scratch> dotnet test tks-auth.slnx -c Release
dotnet publish src/Auth.Anchor/Anchor.csproj -c Release -r linux-x64 -o artifacts/publish   # 0 ILC warnings
(cd web && npm ci && npm run build && npm run check && npm run lint)   # the pages -> web/dist
./deploy/deploy.sh   # both, the pages into /opt/tks-auth/ui beside the binary
```

The pages are checked in a real browser by the workspace harness (`scripts/visual-harness/`):
`oidc-pages.mjs`, `p5-access.mjs` and `p5-applications.mjs`. The packages are referenced by projects in
kgsm-api, kgsm-assistant, kgsm-bot and kgsm-dns: build those before declaring work done.

## Read before changing

- `../kgsm-docs/systems/identity/`: accounts, sign-in, sessions, applications, the anchor in a cluster.
- `../kgsm-docs/systems/authorization/`: the access model.
- `../kgsm-docs/reference/oidc-provider.md`, `session-tokens.md`, `auth-cluster-wire.md`,
  `action-manifest.md`, `operation-manifest.md`: the contracts.
- `README.md`, `web/CLAUDE.md`, and the `CLAUDE.md` beside each package.

## Version tracking

Each package versions on its own clock, and the daemon on one of its own. `deploy/version.sh` reads the
daemon's, because that is the one the pacman package ships.

**Tags carry the prefix of the thing they version:** `auth-v*`, `access-v*`, `users-v*`, `journal-v*`,
`cluster-v*`, `testing-v*` for the packages, and a bare `v*` for the daemon. Only the bare `v*` fires the
release workflow, which asserts the tag against `deploy/version.sh`.

- **Version source:** each package's `<Version>` in its own csproj; the daemon's in
  `src/Auth.Anchor/Anchor.csproj`.
- Bump on any user-facing change; patch for fixes, minor for additions, major for a breaking change, with a
  `CHANGELOG.md` entry under `## [Unreleased]`.
- Consumers pin a version from the org's GitHub Packages feed, so shipping a change means bump the version,
  publish, then bump the pin: `../scripts/publish-packages.sh tks-auth`.
