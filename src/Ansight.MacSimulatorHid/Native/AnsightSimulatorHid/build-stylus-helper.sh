#!/usr/bin/env bash
set -euo pipefail

native_directory="$(cd "$(dirname "$0")" && pwd)"
architecture="$1"
output="$2"
simulator_sdk="$(xcrun --sdk iphonesimulator --show-sdk-path)"

xcrun --sdk iphonesimulator clang \
    -target "$architecture-apple-ios18.0-simulator" -isysroot "$simulator_sdk" \
    -fobjc-arc -fblocks -Wall -Wextra -Werror \
    "$native_directory/_Xcode27SimulatorStylusGuest.m" \
    -framework Foundation -framework IOKit -o "$output"
/usr/bin/codesign --force --sign - "$output"
