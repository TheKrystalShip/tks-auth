# CLAUDE.md — Sessions

`TheKrystalShip.Auth.Sessions` and `TheKrystalShip.Auth.Sessions.Testing`: the one way a service checks a
tks-auth access token. Usage is `README.md`; the tests in `tests/Sessions.Tests` are the specification.

- **The only published projects in this repo.** They version together on their own clock, the `<Version>`
  in both csproj files, tagged `sessions-v<version>`. Publish with `./scripts/publish-packages.sh tks-auth`
  from the workspace root; a version is immutable once pushed.
- **AOT-safe, zero warnings.** Every KGSM leaf embeds this in a Native-AOT binary: options are read key by
  key (never the configuration binder), and every body read or written goes through `SessionsJsonContext`.
- **Token rules are one place.** A service never sets its own `TokenValidationParameters`; a rule that
  changes here changes for every service at once, which is the point.
- **What varies per service is an option, never a fork:** the section, the error body, the declared
  actions, the hook on admission, and other schemes a route also takes.
