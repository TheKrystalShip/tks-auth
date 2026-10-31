#!/usr/bin/env bash
#
# deploy-app.sh — build the Android app's release and publish it at /ui/auth.apk. Fully headless: no
# sudo, no prompts, nothing restarted.
#
#   ./deploy/deploy-app.sh
#
# Builds android/'s release (android/build.sh, for the issuer shell.local.conf names) and puts it in
# the pages' directory as auth.apk, where the daemon serves it beside the pages and their footer
# offers it on an Android phone's browser. deploy.sh's sync leaves that file in place. The daemon
# reads it from disk per request, so nothing is restarted; the file is written under a temporary name
# and renamed, so a download in progress never reads half of one release and half of the next. The
# deploy is verified by fetching /ui/auth.apk from the daemon and comparing it with the build.
#
# The release key lives in ~/.config/tks-auth-app/ (android/README.md).
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

trap 'err "app deploy failed (line $LINENO)."; exit 1' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup
[[ -d "$PREFIX/ui" ]] || { err "${PREFIX}/ui is missing: run deploy/deploy.sh first"; exit 1; }

# ── 1. Build ──────────────────────────────────────────────────────────────────
log "building the Android app's release"
apk="$("$REPO_DIR/android/build.sh" | tail -n 1 | cut -d' ' -f1)"
[[ -f "$apk" ]] || { err "the build named no APK"; exit 1; }

# ── 2. Publish ────────────────────────────────────────────────────────────────
log "publishing $(basename "$apk") → ${PREFIX}/ui/auth.apk"
cp "$apk" "$PREFIX/ui/.auth.apk.part"
mv -f "$PREFIX/ui/.auth.apk.part" "$PREFIX/ui/auth.apk"

# ── 3. Verify ─────────────────────────────────────────────────────────────────
built="$(md5sum < "$apk")"
served="$(curl -sf "http://127.0.0.1:${AUTH_PORT}/ui/auth.apk" | md5sum)"
[[ "$built" == "$served" ]] || { err "the daemon does not serve the release just published at /ui/auth.apk"; exit 1; }
log "the app is live at /ui/auth.apk ✓"
