#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd "$(dirname "$0")" && pwd)"
test_directory="$(mktemp -d "${TMPDIR:-/tmp}/ansight-stylus-signature-tests.XXXXXX")"
trap 'rm -rf "$test_directory"' EXIT

helper="${test_directory}/SimulatorGuest"
entitlements="${test_directory}/entitlements.plist"
verifier="${script_directory}/verify-simulator-stylus-signature.sh"

/usr/bin/xcrun --sdk iphonesimulator clang \
  -target "$(/usr/bin/uname -m)-apple-ios18.0-simulator" \
  -x c - -o "$helper" <<'SOURCE'
int main(void)
{
    return 0;
}
SOURCE

for variant in missing-both missing-dyld false-dyld missing-library false-library complete; do
  cp "${script_directory}/ansight-simulator-stylus.entitlements.plist" "$entitlements"
  case "$variant" in
    missing-both)
      /usr/libexec/PlistBuddy -c "Clear dict" "$entitlements"
      ;;
    missing-dyld)
      /usr/libexec/PlistBuddy -c "Delete :com.apple.security.cs.allow-dyld-environment-variables" "$entitlements"
      ;;
    false-dyld)
      /usr/libexec/PlistBuddy -c "Set :com.apple.security.cs.allow-dyld-environment-variables false" "$entitlements"
      ;;
    missing-library)
      /usr/libexec/PlistBuddy -c "Delete :com.apple.security.cs.disable-library-validation" "$entitlements"
      ;;
    false-library)
      /usr/libexec/PlistBuddy -c "Set :com.apple.security.cs.disable-library-validation false" "$entitlements"
      ;;
  esac

  /usr/bin/codesign --force --sign - --options runtime --entitlements "$entitlements" "$helper"
  /usr/bin/codesign --verify --strict "$helper"

  if /bin/bash "$verifier" "$helper" > "${test_directory}/verification.log" 2>&1; then
    if [[ "$variant" != complete ]]; then
      echo "FAIL: Pencil signature verifier accepted ${variant}." >&2
      exit 1
    fi
  elif [[ "$variant" == complete ]]; then
    cat "${test_directory}/verification.log" >&2
    exit 1
  fi

  echo "PASS: Pencil signature verification: ${variant}"
done
