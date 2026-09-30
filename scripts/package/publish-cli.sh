#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

CONFIGURATION="${CONFIGURATION:-Release}"
RID="${RID:-linux-x64}"
TARGET_FRAMEWORK="${TARGET_FRAMEWORK:-net10.0}"
OUTPUT_DIR="${OUTPUT_DIR:-${REPO_ROOT}/products/cli/${RID}}"
CLI_PROJECT="${REPO_ROOT}/src/Ansight.Cli/Ansight.Cli.csproj"
PROJECT="${PROJECT:-${CLI_PROJECT}}"
VERSION_FILE="${VERSION_FILE:-${REPO_ROOT}/ansight.version.props}"
WINDOWS_TRAY_PROJECT="${WINDOWS_TRAY_PROJECT:-${REPO_ROOT}/src/Ansight.Tray.Windows/Ansight.Tray.Windows.csproj}"
MACOS_SIMULATOR_RTC_PROJECT="${MACOS_SIMULATOR_RTC_PROJECT:-${REPO_ROOT}/src/Ansight.SimulatorRtc.Mac/Ansight.SimulatorRtc.Mac.csproj}"
MACOS_TRAY_SOURCE="${MACOS_TRAY_SOURCE:-${REPO_ROOT}/src/native/macos/AnsightTray/main.swift}"
MACOS_TRAY_INFO_PLIST="${MACOS_TRAY_INFO_PLIST:-${REPO_ROOT}/src/native/macos/AnsightTray/Info.plist}"
MACOS_APP_ICON_SOURCE="${MACOS_APP_ICON_SOURCE:-${REPO_ROOT}/assets/Ansight Mac icon.icon}"
TRAY_ICON_SOURCE="${TRAY_ICON_SOURCE:-${REPO_ROOT}/assets/icon.png}"
DEFAULT_MACOS_CODESIGN_IDENTITY="Developer ID Application: Ansight, Inc. (P34C7MACUW)"
MACOS_CODESIGN_IDENTITY="${CODESIGN_IDENTITY-}"
MACOS_CODESIGN_IDENTITY_IS_EXPLICIT="${CODESIGN_IDENTITY+x}"
MACOS_BUNDLE_IDENTIFIER="${MACOS_BUNDLE_IDENTIFIER:-com.ansight-ai.cli}"
MACOS_ENTITLEMENTS="${MACOS_ENTITLEMENTS:-${SCRIPT_DIR}/ansight-cli.entitlements.plist}"
INCLUDE_NATIVE_LIBRARIES_FOR_SELF_EXTRACT="true"
DOTNET_RESTORE_ARGUMENTS=()
case "${SKIP_RESTORE:-false}" in
  1|true|TRUE|yes|YES|on|ON)
    DOTNET_RESTORE_ARGUMENTS+=(--no-restore)
    ;;
esac
DOTNET_BUILD_ARGUMENTS=()
if [[ -n "${NUGET_CONFIG_FILE:-}" ]]; then
  DOTNET_BUILD_ARGUMENTS+=("-p:RestoreConfigFile=${NUGET_CONFIG_FILE}")
fi
# Referenced projects can be evaluated without the CLI RuntimeIdentifier.
# Pass the native helper architecture as a global property for cross-publishing.
case "${RID}" in
  osx-x64)
    DOTNET_BUILD_ARGUMENTS+=(
      "-p:AudioInjectionMacArchitecture=x86_64"
      "-p:NativeMacArchitecture=x86_64"
    )
    ;;
  osx-arm64)
    DOTNET_BUILD_ARGUMENTS+=(
      "-p:AudioInjectionMacArchitecture=arm64"
      "-p:NativeMacArchitecture=arm64"
    )
    ;;
esac
if [[ -n "${ANSIGHT_DOTNET_MAX_CPU_COUNT:-}" ]]; then
  if [[ ! "${ANSIGHT_DOTNET_MAX_CPU_COUNT}" =~ ^[1-9][0-9]*$ ]]; then
    echo "ANSIGHT_DOTNET_MAX_CPU_COUNT must be a positive integer." >&2
    exit 1
  fi

  DOTNET_BUILD_ARGUMENTS+=("-m:${ANSIGHT_DOTNET_MAX_CPU_COUNT}")
fi
VERSION="${VERSION:-$(sed -n 's:.*<AnsightReleaseVersion>\(.*\)</AnsightReleaseVersion>.*:\1:p' "${VERSION_FILE}" | head -n 1)}"
BUILD_NUMBER="${BUILD_NUMBER:-$(sed -n 's:.*<AnsightReleaseBuildNumber>\([0-9][0-9]*\)</AnsightReleaseBuildNumber>.*:\1:p' "${VERSION_FILE}" | head -n 1)}"
COMMIT_SHA="${COMMIT_SHA:-$(git -C "${REPO_ROOT}" rev-parse HEAD 2>/dev/null || printf 'unknown')}"

case "${RID}" in
  linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64|win-arm64)
    ;;
  *)
    echo "Unsupported CLI RID '${RID}'." >&2
    exit 1
    ;;
esac

if [[ "${RID}" == win-* && ${#DOTNET_RESTORE_ARGUMENTS[@]} -eq 0 ]]; then
  # The CLI selects its Windows TFM from RuntimeIdentifier. Static graph restore
  # evaluates that property too late on a non-Windows host, so restore the
  # regular project graph first and then restore each conditional Windows TFM
  # explicitly. Static graph restore misses them on a non-Windows host.
  dotnet restore "${PROJECT}" \
    -r "${RID}" \
    ${DOTNET_BUILD_ARGUMENTS[@]+"${DOTNET_BUILD_ARGUMENTS[@]}"} >&2
  if [[ "${PROJECT}" != "${CLI_PROJECT}" ]]; then
    dotnet restore "${PROJECT}" \
      --no-dependencies \
      -r "${RID}" \
      -p:TargetFramework=net10.0-windows10.0.19041.0 \
      -p:EnableWindowsTargeting=true \
      ${DOTNET_BUILD_ARGUMENTS[@]+"${DOTNET_BUILD_ARGUMENTS[@]}"} >&2
  fi
  if [[ -n "${ADDITIONAL_WINDOWS_PROJECT:-}" ]]; then
    dotnet restore "${ADDITIONAL_WINDOWS_PROJECT}" \
      --no-dependencies \
      -r "${RID}" \
      -p:TargetFramework=net10.0-windows10.0.19041.0 \
      -p:EnableWindowsTargeting=true \
      ${DOTNET_BUILD_ARGUMENTS[@]+"${DOTNET_BUILD_ARGUMENTS[@]}"} >&2
  fi
  dotnet restore "${CLI_PROJECT}" \
    --no-dependencies \
    -r "${RID}" \
    -p:TargetFramework=net10.0-windows10.0.19041.0 \
    -p:EnableWindowsTargeting=true \
    ${DOTNET_BUILD_ARGUMENTS[@]+"${DOTNET_BUILD_ARGUMENTS[@]}"} >&2
  DOTNET_RESTORE_ARGUMENTS+=(--no-restore)
fi

if [[ ! "${VERSION}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]]; then
  echo "Invalid CLI version '${VERSION}'." >&2
  exit 1
fi

if [[ ! "${BUILD_NUMBER}" =~ ^[0-9]+$ || "${BUILD_NUMBER}" == "0" ]]; then
  echo "CLI BUILD_NUMBER must be a positive integer: '${BUILD_NUMBER}'." >&2
  exit 1
fi

if [[ "${RID}" == osx-* ]]; then
  INCLUDE_NATIVE_LIBRARIES_FOR_SELF_EXTRACT="false"

  # Project-reference builds do not reliably propagate the CLI runtime
  # identifier to this native bridge. Build it explicitly so Intel releases do
  # not accidentally reuse the default arm64 output.
  dotnet build "${MACOS_SIMULATOR_RTC_PROJECT}" \
    -c "${CONFIGURATION}" \
    -r "${RID}" \
    ${DOTNET_BUILD_ARGUMENTS[@]+"${DOTNET_BUILD_ARGUMENTS[@]}"} \
    ${DOTNET_RESTORE_ARGUMENTS[@]+"${DOTNET_RESTORE_ARGUMENTS[@]}"} >&2
fi

rm -rf "${OUTPUT_DIR}/extensions"
dotnet publish "${PROJECT}" \
  -c "${CONFIGURATION}" \
  -r "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:AnsightCliVersion="${VERSION}" \
  -p:AnsightCliBuildNumber="${BUILD_NUMBER}" \
  -p:AnsightCommitSha="${COMMIT_SHA}" \
  -p:IncludeNativeLibrariesForSelfExtract="${INCLUDE_NATIVE_LIBRARIES_FOR_SELF_EXTRACT}" \
  -p:PublishTrimmed=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  ${DOTNET_BUILD_ARGUMENTS[@]+"${DOTNET_BUILD_ARGUMENTS[@]}"} \
  ${DOTNET_RESTORE_ARGUMENTS[@]+"${DOTNET_RESTORE_ARGUMENTS[@]}"} \
  -o "${OUTPUT_DIR}" >&2

if [[ "${ANSIGHT_STATIC_CLOUD_APP:-false}" == "true" ]]; then
  if [[ "${RID}" == win-* ]]; then
    rm -f "${OUTPUT_DIR}/ansight.exe"
    mv "${OUTPUT_DIR}/Ansight.Cloud.App.exe" "${OUTPUT_DIR}/ansight.exe"
  else
    rm -f "${OUTPUT_DIR}/ansight"
    mv "${OUTPUT_DIR}/Ansight.Cloud.App" "${OUTPUT_DIR}/ansight"
  fi
fi

if [[ "${RID}" == win-* ]]; then
  EXECUTABLE="${OUTPUT_DIR}/ansight.exe"
else
  EXECUTABLE="${OUTPUT_DIR}/ansight"
fi

if [[ ! -f "${EXECUTABLE}" ]]; then
  echo "Expected Ansight CLI executable was not produced: ${EXECUTABLE}" >&2
  exit 1
fi

# Remove retired helper executables from reused publish directories.
rm -f "${OUTPUT_DIR}/ansight-video-encoder-macos" "${OUTPUT_DIR}/ansight-audio-injection"

if [[ "${RID}" != win-* ]]; then
  chmod +x "${EXECUTABLE}"
fi

# macOS simulator bridge libraries can flow through transitive publish items,
# but they are not runtime inputs for non-macOS single-file CLI payloads.
if [[ "${RID}" != osx-* ]]; then
  rm -f \
    "${OUTPUT_DIR}/libAnsightSimulatorHid.dylib" \
    "${OUTPUT_DIR}/libAnsightSimulatorRtc.dylib" \
    "${OUTPUT_DIR}/libAnsightAudioInjection.dylib"
fi

if [[ "${RID}" == osx-* ]]; then
  HID_LIBRARY="${OUTPUT_DIR}/libAnsightSimulatorHid.dylib"
  RTC_LIBRARY="${OUTPUT_DIR}/libAnsightSimulatorRtc.dylib"
  SESSION_VIDEO_LIBRARY="${OUTPUT_DIR}/libAnsightSessionVideoMac.dylib"
  AUDIO_INJECTION_LIBRARY="${OUTPUT_DIR}/libAnsightAudioInjection.dylib"
  SIMULATOR_APP_ICON_HELPER="${OUTPUT_DIR}/ansight-app-icon-ios-simulator"
  if [[ ! -f "${AUDIO_INJECTION_LIBRARY}" ]]; then
    echo "Expected macOS audio injection library was not produced: ${AUDIO_INJECTION_LIBRARY}" >&2
    exit 1
  fi
  if [[ ! -f "${SIMULATOR_APP_ICON_HELPER}" ]]; then
    echo "Expected iOS Simulator app icon helper was not produced: ${SIMULATOR_APP_ICON_HELPER}" >&2
    exit 1
  fi
  if [[ ! -f "${HID_LIBRARY}" ]]; then
    echo "Expected macOS Simulator HID library was not produced: ${HID_LIBRARY}" >&2
    exit 1
  fi
  if [[ ! -f "${RTC_LIBRARY}" ]]; then
    echo "Expected macOS Simulator WebRTC library was not produced: ${RTC_LIBRARY}" >&2
    exit 1
  fi
  if [[ ! -f "${SESSION_VIDEO_LIBRARY}" ]]; then
    echo "Expected macOS session video encoder was not produced: ${SESSION_VIDEO_LIBRARY}" >&2
    exit 1
  fi

  case "${RID}" in
    osx-arm64)
      EXPECTED_NATIVE_ARCHITECTURE="arm64"
      ;;
    osx-x64)
      EXPECTED_NATIVE_ARCHITECTURE="x86_64"
      ;;
  esac
  if ! /usr/bin/lipo "${HID_LIBRARY}" -verify_arch "${EXPECTED_NATIVE_ARCHITECTURE}"; then
    echo "Simulator HID library does not contain ${EXPECTED_NATIVE_ARCHITECTURE}: ${HID_LIBRARY}" >&2
    exit 1
  fi
  if ! /usr/bin/lipo "${RTC_LIBRARY}" -verify_arch "${EXPECTED_NATIVE_ARCHITECTURE}"; then
    echo "Simulator WebRTC library does not contain ${EXPECTED_NATIVE_ARCHITECTURE}: ${RTC_LIBRARY}" >&2
    exit 1
  fi
  if ! /usr/bin/lipo "${SESSION_VIDEO_LIBRARY}" -verify_arch "${EXPECTED_NATIVE_ARCHITECTURE}"; then
    echo "Session video encoder does not contain ${EXPECTED_NATIVE_ARCHITECTURE}: ${SESSION_VIDEO_LIBRARY}" >&2
    exit 1
  fi
  if ! /usr/bin/lipo "${AUDIO_INJECTION_LIBRARY}" -verify_arch "${EXPECTED_NATIVE_ARCHITECTURE}"; then
    echo "Audio injection library does not contain ${EXPECTED_NATIVE_ARCHITECTURE}: ${AUDIO_INJECTION_LIBRARY}" >&2
    exit 1
  fi
fi

if [[ "${RID}" == win-* ]]; then
  TRAY_BUILD_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ansight-tray.XXXXXX")"
  dotnet publish "${WINDOWS_TRAY_PROJECT}" \
    -c "${CONFIGURATION}" \
    -r "${RID}" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "${TRAY_BUILD_DIR}" >&2
  if [[ ! -f "${TRAY_BUILD_DIR}/ansight-tray.exe" ]]; then
    echo "Expected Windows tray helper was not produced: ${TRAY_BUILD_DIR}/ansight-tray.exe" >&2
    rm -rf "${TRAY_BUILD_DIR}"
    exit 1
  fi
  cp "${TRAY_BUILD_DIR}/ansight-tray.exe" "${OUTPUT_DIR}/ansight-tray.exe"
  rm -rf "${TRAY_BUILD_DIR}"
fi

if [[ "${RID}" == osx-* ]]; then
  SKIA_LIBRARY_SOURCE="${REPO_ROOT}/src/Ansight.Cli/bin/${CONFIGURATION}/${TARGET_FRAMEWORK}/${RID}/libSkiaSharp.dylib"
  if [[ ! -f "${SKIA_LIBRARY_SOURCE}" ]]; then
    echo "Expected macOS SkiaSharp library was not produced: ${SKIA_LIBRARY_SOURCE}" >&2
    exit 1
  fi
  cp "${SKIA_LIBRARY_SOURCE}" "${OUTPUT_DIR}/libSkiaSharp.dylib"

  case "${RID}" in
    osx-arm64)
      MACOS_TRAY_TARGET="arm64-apple-macos13.0"
      ;;
    osx-x64)
      MACOS_TRAY_TARGET="x86_64-apple-macos13.0"
      ;;
  esac
  TRAY_APP="${OUTPUT_DIR}/Ansight Tray.app"
  TRAY_EXECUTABLE="${TRAY_APP}/Contents/MacOS/ansight-tray"
  TRAY_MODULE_CACHE="$(mktemp -d "${TMPDIR:-/tmp}/ansight-tray-module-cache.XXXXXX")"
  TRAY_ICON_WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ansight-tray-icon.XXXXXX")"
  TRAY_COMPOSER_ICON="${TRAY_ICON_WORK_DIR}/Ansight.icon"
  TRAY_ICON_PARTIAL_PLIST="${TRAY_ICON_WORK_DIR}/partial-info.plist"
  mkdir -p "${TRAY_APP}/Contents/MacOS" "${TRAY_APP}/Contents/Resources"
  cp "${MACOS_TRAY_INFO_PLIST}" "${TRAY_APP}/Contents/Info.plist"
  cp "${TRAY_ICON_SOURCE}" "${TRAY_APP}/Contents/Resources/ansight-icon.png"
  cp -R "${MACOS_APP_ICON_SOURCE}" "${TRAY_COMPOSER_ICON}"
  /usr/bin/xcrun actool \
    "${TRAY_COMPOSER_ICON}" \
    --compile "${TRAY_APP}/Contents/Resources" \
    --platform macosx \
    --minimum-deployment-target 13.0 \
    --app-icon Ansight \
    --output-partial-info-plist "${TRAY_ICON_PARTIAL_PLIST}" \
    --enable-on-demand-resources NO \
    --warnings \
    --notices >/dev/null
  if [[ ! -f "${TRAY_APP}/Contents/Resources/Assets.car" \
        || ! -f "${TRAY_APP}/Contents/Resources/Ansight.icns" ]]; then
    echo "Expected macOS app icon assets were not produced." >&2
    exit 1
  fi
  rm -rf "${TRAY_ICON_WORK_DIR}"
  CLANG_MODULE_CACHE_PATH="${TRAY_MODULE_CACHE}" \
    SWIFT_MODULECACHE_PATH="${TRAY_MODULE_CACHE}" \
    /usr/bin/xcrun --sdk macosx swiftc \
    -parse-as-library \
    -target "${MACOS_TRAY_TARGET}" \
    "${MACOS_TRAY_SOURCE}" \
    -o "${TRAY_EXECUTABLE}"
  rm -rf "${TRAY_MODULE_CACHE}"
  chmod +x "${TRAY_EXECUTABLE}"

  if [[ -z "${MACOS_CODESIGN_IDENTITY_IS_EXPLICIT}" ]] \
    && /usr/bin/security find-identity -v -p codesigning 2>/dev/null \
      | /usr/bin/grep -Fq "${DEFAULT_MACOS_CODESIGN_IDENTITY}"; then
    MACOS_CODESIGN_IDENTITY="${DEFAULT_MACOS_CODESIGN_IDENTITY}"
  fi

  if [[ -n "${MACOS_CODESIGN_IDENTITY}" ]]; then
    if [[ ! -f "${MACOS_ENTITLEMENTS}" ]]; then
      echo "Expected macOS CLI entitlements were not found: ${MACOS_ENTITLEMENTS}" >&2
      exit 1
    fi

    remove_existing_code_signature() {
      local target="$1"
      if /usr/bin/codesign -d "${target}" >/dev/null 2>&1; then
        /usr/bin/codesign --remove-signature "${target}"
      fi
    }

    while IFS= read -r native_library; do
      remove_existing_code_signature "${native_library}"
      /usr/bin/codesign \
        --force \
        --options runtime \
        --timestamp \
        --sign "${MACOS_CODESIGN_IDENTITY}" \
        "${native_library}"
    done < <(/usr/bin/find "${OUTPUT_DIR}" -maxdepth 1 -type f -name '*.dylib' -print)
    remove_existing_code_signature "${SIMULATOR_APP_ICON_HELPER}"
    /usr/bin/codesign \
      --force \
      --options runtime \
      --timestamp \
      --sign "${MACOS_CODESIGN_IDENTITY}" \
      "${SIMULATOR_APP_ICON_HELPER}"
    remove_existing_code_signature "${EXECUTABLE}"
    /usr/bin/codesign \
      --force \
      --options runtime \
      --timestamp \
      --identifier "${MACOS_BUNDLE_IDENTIFIER}" \
      --sign "${MACOS_CODESIGN_IDENTITY}" \
      --entitlements "${MACOS_ENTITLEMENTS}" \
      "${EXECUTABLE}"
    remove_existing_code_signature "${TRAY_EXECUTABLE}"
    /usr/bin/codesign \
      --force \
      --options runtime \
      --timestamp \
      --sign "${MACOS_CODESIGN_IDENTITY}" \
      "${TRAY_EXECUTABLE}"
    remove_existing_code_signature "${TRAY_APP}"
    /usr/bin/codesign \
      --force \
      --options runtime \
      --timestamp \
      --sign "${MACOS_CODESIGN_IDENTITY}" \
      "${TRAY_APP}"
    while IFS= read -r native_library; do
      /usr/bin/codesign --verify --strict --verbose=2 "${native_library}"
    done < <(/usr/bin/find "${OUTPUT_DIR}" -maxdepth 1 -type f -name '*.dylib' -print)
    /usr/bin/codesign --verify --strict --verbose=2 "${SIMULATOR_APP_ICON_HELPER}"
    /usr/bin/codesign --verify --strict --verbose=2 "${EXECUTABLE}"
    /usr/bin/codesign --verify --strict --verbose=2 "${TRAY_APP}"
  else
    echo "Warning: macOS CLI signing is disabled or no Developer ID identity was found; the CLI remains ad-hoc signed and Keychain access may prompt again after rebuilding." >&2
    while IFS= read -r native_library; do
      /usr/bin/codesign --force --sign - "${native_library}"
    done < <(/usr/bin/find "${OUTPUT_DIR}" -maxdepth 1 -type f -name '*.dylib' -print)
    /usr/bin/codesign --force --sign - "${SIMULATOR_APP_ICON_HELPER}"
    /usr/bin/codesign --force --sign - "${EXECUTABLE}"
    /usr/bin/codesign --force --sign - "${TRAY_EXECUTABLE}"
    /usr/bin/codesign --force --sign - "${TRAY_APP}"
  fi

  HOST_NATIVE_ARCHITECTURE="$(/usr/bin/uname -m)"
  if [[ "${HOST_NATIVE_ARCHITECTURE}" == "${EXPECTED_NATIVE_ARCHITECTURE}" \
        && -n "${MACOS_CODESIGN_IDENTITY}" ]]; then
    HID_PROBE_DATA_DIRECTORY="$(mktemp -d "${TMPDIR:-/tmp}/ansight-hid-probe.XXXXXX")"
    HID_PROBE_OUTPUT="${HID_PROBE_DATA_DIRECTORY}/doctor.json"
    if ! "${EXECUTABLE}" doctor \
      --data-dir "${HID_PROBE_DATA_DIRECTORY}/state" \
      --require device.ios.simulator-hid \
      --json >"${HID_PROBE_OUTPUT}"; then
      echo "Packaged Ansight CLI failed its native Simulator HID compatibility probe." >&2
      /bin/cat "${HID_PROBE_OUTPUT}" >&2
      rm -rf "${HID_PROBE_DATA_DIRECTORY}"
      exit 1
    fi
    rm -rf "${HID_PROBE_DATA_DIRECTORY}"
  elif [[ "${HOST_NATIVE_ARCHITECTURE}" == "${EXPECTED_NATIVE_ARCHITECTURE}" ]]; then
    # Ad-hoc rebuilds have a different signing identity and can prompt for
    # Keychain authorization before command dispatch. Keep local builds
    # non-interactive; the signature checks below still verify the payload.
    echo "Skipping the Simulator HID runtime probe for an ad-hoc local build; release archives require Developer ID signing." >&2
  else
    echo "Skipping the runtime Simulator HID probe for ${RID} on ${HOST_NATIVE_ARCHITECTURE}; architecture and signature checks still apply." >&2
  fi

  # Running the final payload must not invalidate its seal. Keep this check
  # after the compatibility probe so post-sign mutation cannot enter an archive.
  while IFS= read -r native_library; do
    /usr/bin/codesign --verify --strict --verbose=2 "${native_library}"
  done < <(/usr/bin/find "${OUTPUT_DIR}" -maxdepth 1 -type f -name '*.dylib' -print)
  /usr/bin/codesign --verify --strict --verbose=2 "${SIMULATOR_APP_ICON_HELPER}"
  /usr/bin/codesign --verify --strict --verbose=2 "${EXECUTABLE}"
  /usr/bin/codesign --verify --deep --strict --verbose=2 "${TRAY_APP}"
fi

echo "${EXECUTABLE}"
