#!/usr/bin/env bash
set -euo pipefail

helper="$1"
# Native requirements handle both XML and DER entitlements. "exists" rejects
# absent entitlements and Boolean false, unlike a text search for the key.
requirement='entitlement["com.apple.security.cs.allow-dyld-environment-variables"] exists'
requirement+=' and entitlement["com.apple.security.cs.disable-library-validation"] exists'

if ! /usr/bin/codesign --verify --strict --verbose=2 -R="$requirement" "$helper"; then
  echo "Simulator Pencil helper must have a valid signature with DYLD environment access and disabled library validation: ${helper}" >&2
  exit 1
fi
