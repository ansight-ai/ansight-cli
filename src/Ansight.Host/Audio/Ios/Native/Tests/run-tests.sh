#!/usr/bin/env bash
set -euo pipefail

test_directory="$(cd "$(dirname "$0")" && pwd)"
test_build_directory="$(mktemp -d "${TMPDIR:-/tmp}/ansight-audio-tests.XXXXXX")"
trap 'rm -rf "$test_build_directory"' EXIT

# Compile tests in the same file to exercise private lifecycle functions without
# adding testing entry points to the shipped library.
cat "$test_directory/../AnsightAudioInjection.swift" "$test_directory/LifecycleTests.swift" > "$test_build_directory/main.swift"
xcrun --sdk macosx swiftc -warnings-as-errors "$test_build_directory/main.swift" -o "$test_build_directory/lifecycle-tests"
"$test_build_directory/lifecycle-tests"
