#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_directory="$(cd "${script_directory}/.." && pwd)"
native_dependencies="${1:?Pass the built native dependency directory.}"
packaged_library="${2:-}"
datachannel_install="${native_dependencies}/libdatachannel-install"
mbedtls_install="${native_dependencies}/mbedtls-install"
test_directory="$(mktemp -d "${TMPDIR:-/tmp}/ansight-webrtc-security.XXXXXX")"
trap 'rm -rf "${test_directory}"' EXIT

if [[ -n "${packaged_library}" ]]; then
  link_libraries=(
    "${packaged_library}"
    "-Wl,-rpath,$(dirname "${packaged_library}")"
  )
else
  link_libraries=(
    "${datachannel_install}/lib/libdatachannel.a"
    "${datachannel_install}/lib/libjuice.a"
    "${datachannel_install}/lib/libsrtp2.a"
    "${datachannel_install}/lib/libusrsctp.a"
    "${mbedtls_install}/lib/libmbedtls.a"
    "${mbedtls_install}/lib/libmbedx509.a"
    "${mbedtls_install}/lib/libmbedcrypto.a"
    "${mbedtls_install}/lib/libeverest.a"
    "${mbedtls_install}/lib/libp256m.a"
  )
fi

/usr/bin/clang++ -std=c++17 \
  -DRTC_STATIC=1 -DRTC_ENABLE_MEDIA=1 -DRTC_ENABLE_WEBSOCKET=0 \
  -I"${datachannel_install}/include" \
  "${repository_directory}/tests/native/WebRtcSecurityTests.cpp" \
  "${link_libraries[@]}" \
  -framework Security -framework CoreFoundation -lpthread \
  -o "${test_directory}/webrtc-security-tests"

"${test_directory}/webrtc-security-tests"
"${test_directory}/webrtc-security-tests" tamper
