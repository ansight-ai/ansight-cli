#!/usr/bin/env bash
set -euo pipefail

test_directory="$(cd "$(dirname "$0")" && pwd)"
test_build_directory="$(mktemp -d "${TMPDIR:-/tmp}/ansight-hid-tests.XXXXXX")"
trap 'rm -rf "$test_build_directory"' EXIT

/bin/bash "$test_directory/../../../../scripts/package/test-simulator-stylus-signature.sh"

xcrun --sdk macosx clang -dynamiclib -Wall -Wextra -Werror \
    -DANSIGHT_FRAMEWORK_FIXTURE_VERSION=26 "$test_directory/SimulatorKitFixture.c" \
    -o "$test_build_directory/legacy.dylib"
xcrun --sdk macosx clang -dynamiclib -Wall -Wextra -Werror \
    -DANSIGHT_FRAMEWORK_FIXTURE_VERSION=27 "$test_directory/SimulatorKitFixture.c" \
    -o "$test_build_directory/shared.dylib"
xcrun --sdk macosx clang -fobjc-arc -fblocks -fmodules -Wall -Wextra -Werror \
    -fmodules-cache-path="$test_build_directory/modules" \
    -fsanitize=address -g \
    "$test_directory/AnsightSimulatorFrameworkTests.m" \
    -framework Foundation \
    -o "$test_build_directory/AnsightSimulatorFrameworkTests"
"$test_build_directory/AnsightSimulatorFrameworkTests" \
    "$test_build_directory" "$test_build_directory/legacy.dylib" "$test_build_directory/shared.dylib"

xcrun --sdk macosx clang -fobjc-arc -fblocks -fmodules -Wall -Wextra -Werror \
    -fmodules-cache-path="$test_build_directory/modules" \
    -fsanitize=address -g \
    "$test_directory/AnsightSimulatorHidRecoveryTests.m" \
    "$test_directory/../AnsightSimulatorHid/_Xcode27SimulatorHid.m" \
    -framework Foundation -framework CoreGraphics \
    -o "$test_build_directory/AnsightSimulatorHidRecoveryTests"
"$test_build_directory/AnsightSimulatorHidRecoveryTests"

xcrun --sdk macosx clang -fobjc-arc -fblocks -fmodules -Wall -Wextra -Werror \
    -fmodules-cache-path="$test_build_directory/modules" \
    -fsanitize=address -g \
    "$test_directory/_Xcode27SimulatorHidTests.m" \
    -framework Foundation \
    -o "$test_build_directory/_Xcode27SimulatorHidTests"
"$test_build_directory/_Xcode27SimulatorHidTests"

xcrun --sdk macosx clang -fobjc-arc -fblocks -fmodules -Wall -Wextra -Werror \
    -fmodules-cache-path="$test_build_directory/modules" \
    -fsanitize=address -g \
    "$test_directory/_Xcode27SimulatorRoutingTests.m" \
    "$test_directory/../AnsightSimulatorHid/_Xcode27SimulatorHid.m" \
    -framework Foundation -framework CoreGraphics \
    -o "$test_build_directory/_Xcode27SimulatorRoutingTests"
"$test_build_directory/_Xcode27SimulatorRoutingTests"
