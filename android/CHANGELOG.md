# Changelog

Auth for Android phones, the app in this directory. Its version clock is its own, tagged
`android-v<version>`; the newest heading here is the version `build.sh` builds.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.0] - 2026-10-30

### Added
- On the TKS shell 1.6.0: a page's vibration is run by the shell (`TksShell.vibrate`), so the first long press of a freshly opened app buzzes.

## [1.1.2] - 2026-10-30

### Fixed
- On the TKS shell 1.5.2: the vibration a page asks for reaches the phone.

## [1.1.1] - 2026-10-23

### Fixed
- On the TKS shell 1.5.1: the page shown when the site cannot be reached no longer names Cinema.

## [1.1.0] - 2026-10-23

### Changed
- On the TKS shell 1.5.0: the launcher icon follows the palette the site shows, switched when the app
  leaves the screen, with an icon per palette written by the site's `npm run icons`.

## [1.0.0] - 2026-10-18

### Added

- **The provider's pages on a phone**: the account page, and the administration for an account that
  administers, in an app of their own on the TKS shell, signed in inside the app; its icon and splash
  Lucide's key in the accent on its tinted ground.
