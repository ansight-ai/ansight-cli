#!/bin/sh
set -eu
# Rebuild the small embedded DEX when AndroidAppIcon.java changes.
: "${ANDROID_SDK_ROOT:?Set ANDROID_SDK_ROOT to the Android SDK directory}"
: "${JAVA_HOME:?Set JAVA_HOME to a JDK directory}"
icon_source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
icon_build_dir=$(mktemp -d)
trap 'rm -rf "$icon_build_dir"' EXIT
"$JAVA_HOME/bin/javac" --release 8 -classpath "$ANDROID_SDK_ROOT/platforms/android-36/android.jar" \
  -d "$icon_build_dir" "$icon_source_dir/AndroidAppIcon.java"
"$ANDROID_SDK_ROOT/build-tools/36.1.0/d8" --min-api 26 \
  --lib "$ANDROID_SDK_ROOT/platforms/android-36/android.jar" --output "$icon_build_dir" \
  "$icon_build_dir/ai/ansight/host/AndroidAppIcon.class"
cp "$icon_build_dir/classes.dex" "$icon_source_dir/android-app-icon.dex"
