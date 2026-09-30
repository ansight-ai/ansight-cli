#!/usr/bin/env bash
set -euo pipefail

test_directory="$(cd "$(dirname "$0")" && pwd)"
test_build_directory="$(mktemp -d "${TMPDIR:-/tmp}/ansight-hid-tests.XXXXXX")"
trap 'rm -rf "$test_build_directory"' EXIT

xcrun --sdk macosx clang -fobjc-arc -fblocks -fmodules -Wall -Wextra -Werror \
    -fsanitize=address -g \
    "$test_directory/AnsightSimulatorHidRecoveryTests.m" \
    -framework Foundation -framework CoreGraphics \
    -o "$test_build_directory/AnsightSimulatorHidRecoveryTests"
"$test_build_directory/AnsightSimulatorHidRecoveryTests"
