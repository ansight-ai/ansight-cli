#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_directory="$(cd "${script_directory}/.." && pwd)"
native_dependencies="${1:?Pass the built native dependency directory.}"
packaged_library="${2:-${native_dependencies}/libAnsightSimulatorRtc.dylib}"
datachannel_install="${native_dependencies}/libdatachannel-install"
test_directory="$(mktemp -d "${TMPDIR:-/tmp}/ansight-video-streaming.XXXXXX")"
trap 'rm -rf "${test_directory}"' EXIT

/usr/bin/clang++ -std=c++17 -Wall -Wextra -Werror \
  "${repository_directory}/tests/native/VideoFlowControlTests.cpp" \
  -o "${test_directory}/video-flow-control-tests"
"${test_directory}/video-flow-control-tests"

/usr/bin/clang++ -std=c++17 \
  -DRTC_STATIC=1 -DRTC_ENABLE_MEDIA=1 -DRTC_ENABLE_WEBSOCKET=0 \
  -I"${datachannel_install}/include" \
  "${repository_directory}/tests/native/VideoPlayoutTests.cpp" \
  "${packaged_library}" "-Wl,-rpath,$(dirname "${packaged_library}")" \
  -o "${test_directory}/video-playout-tests"
"${test_directory}/video-playout-tests"

/usr/bin/clang++ -std=c++17 -fobjc-arc -fblocks \
  -DRTC_STATIC=1 -DRTC_ENABLE_MEDIA=1 -DRTC_ENABLE_WEBSOCKET=0 \
  -I"${datachannel_install}/include" \
  "${repository_directory}/tests/native/VideoStreamingTests.mm" \
  "${packaged_library}" \
  "-Wl,-rpath,$(dirname "${packaged_library}")" \
  -framework Foundation -framework CoreVideo -framework CoreMedia \
  -framework IOSurface -framework VideoToolbox -framework Security -lpthread \
  -o "${test_directory}/video-streaming-tests"

"${test_directory}/video-streaming-tests" "${packaged_library}"
