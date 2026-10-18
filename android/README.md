# Auth for Android phones

The provider's own pages as an app, on the TKS shell: the account page, and the administration for an
account that administers, in a WebView signed in at the provider inside the app. It opens at the
provider's origin, which opens the account page. What the shell does and how an app is built on it is
the shell's `README.md`, vendored here as `shell/` (`shell/VENDORED` names its version) and never
edited here; how a site becomes an app, and the bridge, are `kgsm-docs`' `systems/shell/README.md` and
`reference/shell-bridge.md`.

## What is the provider's

- **`shell.conf`**: the app id every install is made under, which never changes; the mark
  `TksAuthApp`; the artifact name (`tks-auth-app`, also the key directory); no features. The site is
  the issuer this installation runs, `ORIGIN` in the untracked `shell.local.conf` beside it, so the
  repository names no host; without it, `--origin` gives the site.
- **`res/`**: the name, and the icon: Lucide's `key-round` stroked in the accent on its tinted ground,
  as every app on the shell draws its glyph, as an adaptive vector icon with a themed variant. It is
  also the splash Android draws while the app starts. `debug/res/` names the debug build.
- **`CHANGELOG.md`**: the app's version, a clock of its own tagged `android-v<version>`.

## Building

```bash
android/build.sh                          # release, for the site shell.local.conf names
android/build.sh --origin https://host    # release, for another site
android/build.sh --debug --install --open # debug build, installed and opened on the phone adb reaches
```

`build.sh` hands this directory to the shell's build (`shell/build.sh`), which says what each option
does. The APK lands in `android/out/`. A release is signed with the key in `~/.config/tks-auth-app/`,
which is never in a repository and has to be kept: Android installs an update only under the key the
app was installed with. The debug build is `com.thekrystalship.auth.debug`, installed beside a release
one, with its WebView open to the DevTools protocol.
