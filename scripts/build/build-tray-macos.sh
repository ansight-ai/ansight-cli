#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: build-tray-macos.sh <output-directory> <osx-arm64|osx-x64>" >&2
  exit 2
fi

output_dir="$1"
case "$2" in
  osx-arm64) target="arm64-apple-macos13.0" ;;
  osx-x64) target="x86_64-apple-macos13.0" ;;
  *) echo "Unsupported macOS tray RID: $2" >&2; exit 2 ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
app="$output_dir/Ansight Tray.app"
icon_work_dir="$(mktemp -d "${TMPDIR:-/tmp}/ansight-tray-icon.XXXXXX")"
module_cache="$(mktemp -d "${TMPDIR:-/tmp}/ansight-tray-module-cache.XXXXXX")"
trap 'rm -rf "$icon_work_dir" "$module_cache"' EXIT

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$repo_root/src/native/macos/AnsightTray/Info.plist" "$app/Contents/Info.plist"
cp "$repo_root/assets/icon.png" "$app/Contents/Resources/ansight-icon.png"
cp -R "$repo_root/assets/Ansight Mac icon.icon" "$icon_work_dir/Ansight.icon"
/usr/bin/xcrun actool \
  "$icon_work_dir/Ansight.icon" \
  --compile "$app/Contents/Resources" \
  --platform macosx \
  --minimum-deployment-target 13.0 \
  --app-icon Ansight \
  --output-partial-info-plist "$icon_work_dir/partial-info.plist" \
  --enable-on-demand-resources NO \
  --warnings \
  --notices >/dev/null

test -f "$app/Contents/Resources/Assets.car"
test -f "$app/Contents/Resources/Ansight.icns"
CLANG_MODULE_CACHE_PATH="$module_cache" \
SWIFT_MODULECACHE_PATH="$module_cache" \
  /usr/bin/xcrun --sdk macosx swiftc \
  -parse-as-library \
  -target "$target" \
  "$repo_root/src/native/macos/AnsightTray/main.swift" \
  -o "$app/Contents/MacOS/ansight-tray"
chmod +x "$app/Contents/MacOS/ansight-tray"
/usr/bin/codesign --force --sign - "$app"
/usr/bin/codesign --verify "$app"
