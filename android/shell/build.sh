#!/usr/bin/env bash
# Builds an app on the TKS shell with the SDK's own tools: aapt2, javac, d8, zipalign and apksigner.
# No Gradle.
#
#   shell/build.sh <app-dir> [--debug] [--origin https://host] [--install] [--open [url]]
#
#   <app-dir>   the app: its shell.conf, CHANGELOG.md (the version), res/ (its name and icon, laid over
#               the shell's), and debug/res/ (the debug build's, laid over both); this directory is
#               the shell, vendored beside them as <app-dir>/shell/
#   --debug     WebView debugging on, plain http to the development machine's loopback, the address
#               taken from the launch's `url` extra; installs beside the release build as <APP_ID>.debug
#               and is signed with a debug key made on first use
#   --origin    the site the app shows (shell.conf's ORIGIN); a release build takes https only
#   --install   installs on the device `adb` reaches ($ANDROID_SERIAL picks one of several)
#   --open      launches it, a debug build on `url` when one is given
#
# shell.conf (sourced as shell):
#   APP_ID      the app id, which an update must keep: com.example.app
#   MARK        what the WebView's user agent carries before the shell's own: MARK/<version>
#   ORIGIN      the https site a release shows; may come from the app's own untracked settings, or
#               be left empty for --origin to give
#   ARTIFACT    the APK's name, <ARTIFACT>-<version>.apk, and the key directory, ~/.config/<ARTIFACT>/
#   FEATURES    what the app does beyond showing its site, space-separated: downloads
#
# The version is the newest heading in the app's CHANGELOG.md, its versionCode major*10000 +
# minor*100 + patch. A release is signed with the key in ~/.config/<ARTIFACT>/ (release.keystore, its
# password in release.password), which stays off every repository: Android updates an app only under
# the key it was installed with. The APK lands in <app-dir>/out/.
#
# The toolchain: JAVA_HOME (~/Android/jdk) and ANDROID_HOME (~/Android/Sdk, with build-tools;35.0.0,
# platforms;android-35 and platform-tools).
set -euo pipefail

shell="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
app="$(cd "${1:?shell/build.sh needs the app directory}" && pwd)"
shift
export JAVA_HOME="${JAVA_HOME:-$HOME/Android/jdk}"
sdk="${ANDROID_HOME:-$HOME/Android/Sdk}"
tools="$sdk/build-tools/35.0.0"
platform="$sdk/platforms/android-35/android.jar"
adb="$sdk/platform-tools/adb"

APP_ID="" MARK="" ORIGIN="" ARTIFACT="" FEATURES=""
# shellcheck source=/dev/null
source "$app/shell.conf"
[[ -n "$APP_ID" && -n "$MARK" && -n "$ARTIFACT" ]] \
  || { echo "$app/shell.conf names APP_ID, MARK and ARTIFACT." >&2; exit 2; }
keys="$HOME/.config/$ARTIFACT"
downloads=false
for feature in $FEATURES; do
  case "$feature" in
    downloads) downloads=true ;;
    *) echo "shell.conf names a feature the shell does not have: $feature" >&2; exit 2 ;;
  esac
done

debug=false origin="$ORIGIN" install=false open=false url=""
while (( $# )); do
  case "$1" in
    --debug) debug=true ;;
    --origin) origin="${2:?--origin needs an address}"; shift ;;
    --install) install=true ;;
    --open) open=true; if [[ "${2:-}" != --* && -n "${2:-}" ]]; then url="$2"; shift; fi ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

[[ -x "$JAVA_HOME/bin/javac" && -x "$tools/d8" && -f "$platform" ]] \
  || { echo "No Android toolchain: JAVA_HOME=$JAVA_HOME, ANDROID_HOME=$sdk" >&2; exit 1; }
[[ -n "$origin" ]] || { echo "No site to show: $app/shell.conf gives no ORIGIN, and no --origin was given." >&2; exit 2; }
origin="${origin%/}"
[[ "$origin" =~ ^https?://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || { echo "--origin is a scheme and a host, nothing more: $origin" >&2; exit 2; }
if ! $debug && [[ "$origin" != https://* ]]; then echo "A release build shows an https site." >&2; exit 2; fi

version="$(sed -n 's/^## \[\([0-9][^]]*\)\].*/\1/p' "$app/CHANGELOG.md" | head -n 1)"
IFS=. read -r major minor patch <<< "${version%%-*}"
code=$(( major * 10000 + minor * 100 + patch ))

kind=$($debug && echo debug || echo release)
out="$app/out/$kind"
rm -rf "$out"
mkdir -p "$out/gen/com/thekrystalship/shell" "$out/classes" "$out/dex"

cat > "$out/gen/com/thekrystalship/shell/Built.java" <<EOF
package com.thekrystalship.shell;

/** What this build was made with, by shell/build.sh from the app's shell.conf. */
final class Built {
    static final String ORIGIN = "$origin";
    static final String VERSION = "$version";
    static final boolean DEBUG = $debug;
    static final String MARK = "$MARK";
    static final String FEATURES = "$FEATURES";
    static final boolean DOWNLOADS = $downloads;

    private Built() {}
}
EOF

# The manifest: the shell's components under the shell's package, renamed to the app's id when linked.
{
  cat <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="com.thekrystalship.shell">
  <uses-sdk android:minSdkVersion="26" android:targetSdkVersion="35" />
  <uses-permission android:name="android.permission.INTERNET" />
EOF
  if $downloads; then cat <<'EOF'
  <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
  <uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
  <uses-permission android:name="android.permission.FOREGROUND_SERVICE_DATA_SYNC" />
  <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
  <uses-permission android:name="android.permission.WAKE_LOCK" />
EOF
  fi
  cat <<'EOF'
  <application
      android:label="@string/app_name"
      android:icon="@mipmap/icon"
      android:allowBackup="false"
      android:networkSecurityConfig="@xml/network_security_config"
      android:theme="@android:style/Theme.DeviceDefault.NoActionBar">
    <activity android:name="com.thekrystalship.shell.ShellActivity" android:exported="true"
        android:launchMode="singleTask"
        android:configChanges="orientation|screenSize|screenLayout|smallestScreenSize|keyboard|keyboardHidden|navigation|uiMode|density">
      <intent-filter>
        <action android:name="android.intent.action.MAIN" />
        <category android:name="android.intent.category.LAUNCHER" />
      </intent-filter>
    </activity>
EOF
  if $downloads; then cat <<'EOF'
    <service android:name="com.thekrystalship.shell.DownloadService" android:exported="false"
        android:foregroundServiceType="dataSync" />
EOF
  fi
  cat <<'EOF'
  </application>
</manifest>
EOF
} > "$out/AndroidManifest.xml"

# The resources first, the app's over the shell's: linking them writes R.java, which the code names
# them by, into the shell's package.
"$tools/aapt2" compile --dir "$shell/res" -o "$out/shell-res.zip"
"$tools/aapt2" compile --dir "$app/res" -o "$out/app-res.zip"
link=(--manifest "$out/AndroidManifest.xml" -I "$platform" -A "$shell/assets"
  --version-code "$code" --version-name "$version" "$out/shell-res.zip" -R "$out/app-res.zip" --auto-add-overlay
  --rename-manifest-package "$APP_ID$($debug && echo .debug || true)")
if $debug; then
  "$tools/aapt2" compile --dir "$shell/debug/res" -o "$out/shell-debug-res.zip"
  link+=(-R "$out/shell-debug-res.zip" --debug-mode)
  if [[ -d "$app/debug/res" ]]; then
    "$tools/aapt2" compile --dir "$app/debug/res" -o "$out/app-debug-res.zip"
    link+=(-R "$out/app-debug-res.zip")
  fi
fi
"$tools/aapt2" link -o "$out/base.apk" --java "$out/gen" --custom-package com.thekrystalship.shell "${link[@]}"

"$JAVA_HOME/bin/javac" -source 8 -target 8 -nowarn -Xlint:-options -bootclasspath "$platform" \
  -d "$out/classes" "$shell"/src/com/thekrystalship/shell/*.java "$out"/gen/com/thekrystalship/shell/*.java
"$tools/d8" --min-api 26 --lib "$platform" $($debug || echo --release) --output "$out/dex" \
  $(find "$out/classes" -name '*.class')
(cd "$out/dex" && zip -q "$out/base.apk" classes.dex)
"$tools/zipalign" -f 4 "$out/base.apk" "$out/aligned.apk"

if $debug; then
  store="$keys/debug.keystore" pass="pass:android"
  if [[ ! -f "$store" ]]; then
    mkdir -p "$keys" && chmod 700 "$keys"
    "$JAVA_HOME/bin/keytool" -genkeypair -keystore "$store" -storepass android -keypass android \
      -alias debug -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=$MARK Debug" >/dev/null 2>&1
  fi
  apk="$app/out/$ARTIFACT-$version-debug.apk"
else
  store="$keys/release.keystore" pass="file:$keys/release.password"
  [[ -f "$store" && -f "$keys/release.password" ]] || {
    echo "No release key in $keys. Make it once, and keep a copy somewhere safe:" >&2
    echo "  mkdir -p $keys && chmod 700 $keys && umask 077 && head -c 24 /dev/urandom | base64 > $keys/release.password" >&2
    echo "  keytool -genkeypair -keystore $keys/release.keystore -storepass:file $keys/release.password \\" >&2
    echo "    -alias release -keyalg RSA -keysize 4096 -validity 36500 -dname \"CN=$MARK\"" >&2
    exit 1
  }
  apk="$app/out/$ARTIFACT-$version.apk"
fi
"$tools/apksigner" sign --ks "$store" --ks-pass "$pass" --out "$apk" "$out/aligned.apk" 2>/dev/null
rm -f "$apk.idsig"
echo "$apk ($kind $version, code $code, $origin)"

package="$APP_ID$($debug && echo .debug || true)"
if $install; then "$adb" install -r "$apk" >/dev/null && echo "installed $package"; fi
if $open; then
  "$adb" shell am force-stop "$package"
  extra=(); [[ -n "$url" ]] && extra=(--es url "$url")
  "$adb" shell am start -n "$package/com.thekrystalship.shell.ShellActivity" "${extra[@]}" >/dev/null
  echo "opened $package"
fi
