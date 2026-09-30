#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

VERSION_FILE="${VERSION_FILE:-${REPO_ROOT}/ansight.version.props}"
RID="${RID:-linux-x64}"
VERSION="${VERSION:-}"
BUILD_NUMBER="${BUILD_NUMBER:-}"
PACKAGE_DIR="${PACKAGE_DIR:-${REPO_ROOT}/products/cli/packages}"
PUBLISH_SCRIPT="${PUBLISH_SCRIPT:-${SCRIPT_DIR}/publish-cli.sh}"
APP_INSPECTION_SKILL="${APP_INSPECTION_SKILL:-${REPO_ROOT}/skills/agents/ansight-app-inspection.md}"
AGENT_SKILLS_ROOT="${AGENT_SKILLS_ROOT:-${REPO_ROOT}/skills/agents}"
CLI_SETUP_SKILL="${CLI_SETUP_SKILL:-${REPO_ROOT}/skills/ansight-cli-setup.md}"
NOTARYTOOL_PROFILE="${NOTARYTOOL_PROFILE:-}"
DITTO="${DITTO:-/usr/bin/ditto}"
XCRUN="${XCRUN:-/usr/bin/xcrun}"
EXPECTED_MACOS_SIGNING_AUTHORITY="Developer ID Application: Ansight, Inc. (P34C7MACUW)"
EXPECTED_MACOS_TEAM_ID="P34C7MACUW"

is_true() {
  case "${1:-}" in
    1|true|TRUE|yes|YES|y|Y|on|ON)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

verify_macos_release_signature() {
  local executable="$1"
  local signature_details

  /usr/bin/codesign --verify --strict --verbose=2 "${executable}"
  signature_details="$(/usr/bin/codesign -dv --verbose=4 "${executable}" 2>&1)"
  if /usr/bin/grep -Fq "Authority=${EXPECTED_MACOS_SIGNING_AUTHORITY}" <<<"${signature_details}" \
    && /usr/bin/grep -Fq "TeamIdentifier=${EXPECTED_MACOS_TEAM_ID}" <<<"${signature_details}"; then
    return
  fi

  if is_true "${ALLOW_ADHOC_MACOS_CLI:-}"; then
    echo "Warning: allowing an ad-hoc macOS CLI archive because ALLOW_ADHOC_MACOS_CLI is enabled." >&2
    return
  fi

  echo "macOS CLI release archives must be signed by ${EXPECTED_MACOS_SIGNING_AUTHORITY}." >&2
  echo "Set ALLOW_ADHOC_MACOS_CLI=true only for a local, non-distributable package." >&2
  exit 1
}

case "${RID}" in
  linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64|win-arm64)
    ;;
  *)
    echo "Unsupported CLI RID '${RID}'." >&2
    exit 1
    ;;
esac

if [[ -z "${VERSION}" ]]; then
  VERSION="$(sed -n 's:.*<AnsightReleaseVersion>\(.*\)</AnsightReleaseVersion>.*:\1:p' \
    "${VERSION_FILE}" | head -n 1)"
fi

if [[ -z "${BUILD_NUMBER}" ]]; then
  BUILD_NUMBER="$(sed -n 's:.*<AnsightReleaseBuildNumber>\([0-9][0-9]*\)</AnsightReleaseBuildNumber>.*:\1:p' \
    "${VERSION_FILE}" | head -n 1)"
fi

if [[ ! "${VERSION}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]]; then
  echo "VERSION must contain only letters, numbers, dots, underscores, and hyphens." >&2
  exit 1
fi


if [[ ! "${BUILD_NUMBER}" =~ ^[0-9]+$ || "${BUILD_NUMBER}" == "0" ]]; then
  echo "BUILD_NUMBER must be a positive integer." >&2
  exit 1
fi

if [[ ! -x "${PUBLISH_SCRIPT}" ]]; then
  echo "CLI publish script is missing or not executable: ${PUBLISH_SCRIPT}" >&2
  exit 1
fi

if [[ ! -f "${APP_INSPECTION_SKILL}" ]]; then
  echo "Ansight app-inspection skill is missing: ${APP_INSPECTION_SKILL}" >&2
  exit 1
fi

if [[ ! -f "${CLI_SETUP_SKILL}" ]]; then
  echo "Ansight CLI setup skill is missing: ${CLI_SETUP_SKILL}" >&2
  exit 1
fi

CORE_AGENT_SKILLS=(
  ansight-annotate-session
  ansight-assess-automation-readiness
  ansight-investigate-session
  ansight-operate-live-app
  ansight-ui-testing
  ansight-use-remote-app-tools
)
for skill in "${CORE_AGENT_SKILLS[@]}"; do
  if [[ ! -f "${AGENT_SKILLS_ROOT}/${skill}/SKILL.md" ]]; then
    echo "Ansight core agent skill is missing: ${AGENT_SKILLS_ROOT}/${skill}/SKILL.md" >&2
    exit 1
  fi
done

mkdir -p "${PACKAGE_DIR}"
STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ansight-cli-${RID}.XXXXXX")"
NOTARIZATION_ZIP="${STAGING_DIR}.notarization.zip"
ARCHIVE_VERIFY_DIR=""
cleanup() {
  rm -rf "${STAGING_DIR}"
  rm -f "${NOTARIZATION_ZIP}"
  if [[ -n "${ARCHIVE_VERIFY_DIR}" ]]; then
    rm -rf "${ARCHIVE_VERIFY_DIR}"
  fi
}
trap cleanup EXIT

RID="${RID}" VERSION="${VERSION}" BUILD_NUMBER="${BUILD_NUMBER}" \
  OUTPUT_DIR="${STAGING_DIR}" "${PUBLISH_SCRIPT}" >/dev/null

cp "${REPO_ROOT}/LICENSE" "${STAGING_DIR}/LICENSE"
cp "${REPO_ROOT}/NOTICE" "${STAGING_DIR}/NOTICE"

mkdir -p "${STAGING_DIR}/skills/ansight-app-inspection"
cp "${APP_INSPECTION_SKILL}" \
  "${STAGING_DIR}/skills/ansight-app-inspection/SKILL.md"
mkdir -p "${STAGING_DIR}/skills/ansight-cli-setup"
cp "${CLI_SETUP_SKILL}" \
  "${STAGING_DIR}/skills/ansight-cli-setup/SKILL.md"
for skill in "${CORE_AGENT_SKILLS[@]}"; do
  mkdir -p "${STAGING_DIR}/skills/${skill}"
  cp -R "${AGENT_SKILLS_ROOT}/${skill}/." \
    "${STAGING_DIR}/skills/${skill}/"
done

if [[ "${RID}" == win-* ]]; then
  EXECUTABLE="${STAGING_DIR}/ansight.exe"
else
  EXECUTABLE="${STAGING_DIR}/ansight"
fi

if [[ ! -f "${EXECUTABLE}" ]]; then
  echo "Expected packaged CLI executable was not found: ${EXECUTABLE}" >&2
  exit 1
fi

if [[ "${RID}" == osx-* ]]; then
  for required_native_library in \
    libAnsightSimulatorHid.dylib \
    libAnsightSimulatorRtc.dylib; do
    if [[ ! -f "${STAGING_DIR}/${required_native_library}" ]]; then
      echo "Expected packaged macOS native library was not found: ${required_native_library}" >&2
      exit 1
    fi
  done
  verify_macos_release_signature "${EXECUTABLE}"
  verify_macos_release_signature "${STAGING_DIR}/ansight-app-icon-ios-simulator"
  if [[ ! -f "${STAGING_DIR}/libAnsightAudioInjection.dylib" ]]; then
    echo "Expected packaged macOS audio injection library was not found." >&2
    exit 1
  fi
  verify_macos_release_signature "${STAGING_DIR}/libAnsightAudioInjection.dylib"
fi

if [[ "${RID}" == osx-* && -n "${NOTARYTOOL_PROFILE}" ]]; then
  if is_true "${SKIP_NOTARIZATION:-}"; then
    echo "Skipping CLI notarization because SKIP_NOTARIZATION is enabled." >&2
  else
    if [[ ! -x "${DITTO}" ]]; then
      echo "ditto was not found: ${DITTO}" >&2
      exit 1
    fi
    if [[ ! -x "${XCRUN}" ]]; then
      echo "xcrun was not found: ${XCRUN}" >&2
      exit 1
    fi

    echo "Submitting macOS CLI payload for notarization..." >&2
    "${DITTO}" -c -k --keepParent "${STAGING_DIR}" "${NOTARIZATION_ZIP}"
    "${XCRUN}" notarytool submit "${NOTARIZATION_ZIP}" \
      --keychain-profile "${NOTARYTOOL_PROFILE}" \
      --wait >&2

    TRAY_APP="${STAGING_DIR}/Ansight Tray.app"
    if [[ -d "${TRAY_APP}" ]]; then
      "${XCRUN}" stapler staple "${TRAY_APP}" >&2
      "${XCRUN}" stapler validate "${TRAY_APP}" >&2
    fi
  fi
fi

ARCHIVE_NAME="ansight-cli-${RID}-${VERSION}-${BUILD_NUMBER}.tar.gz"
ARCHIVE_PATH="${PACKAGE_DIR}/${ARCHIVE_NAME}"
CHECKSUM_PATH="${ARCHIVE_PATH}.sha256"

tar -czf "${ARCHIVE_PATH}" -C "${STAGING_DIR}" .

if [[ "${RID}" == osx-* ]]; then
  ARCHIVE_VERIFY_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ansight-cli-verify-${RID}.XXXXXX")"
  tar -xzf "${ARCHIVE_PATH}" -C "${ARCHIVE_VERIFY_DIR}"
  while IFS= read -r native_library; do
    /usr/bin/codesign --verify --strict --verbose=2 "${native_library}"
  done < <(/usr/bin/find "${ARCHIVE_VERIFY_DIR}" -maxdepth 1 -type f -name '*.dylib' -print)
  verify_macos_release_signature "${ARCHIVE_VERIFY_DIR}/ansight"
  verify_macos_release_signature "${ARCHIVE_VERIFY_DIR}/ansight-app-icon-ios-simulator"
  verify_macos_release_signature "${ARCHIVE_VERIFY_DIR}/libAnsightAudioInjection.dylib"
  /usr/bin/codesign --verify --deep --strict --verbose=2 \
    "${ARCHIVE_VERIFY_DIR}/Ansight Tray.app"
  if [[ -n "${NOTARYTOOL_PROFILE}" ]] && ! is_true "${SKIP_NOTARIZATION:-}"; then
    "${XCRUN}" stapler validate "${ARCHIVE_VERIFY_DIR}/Ansight Tray.app" >&2
  fi
  rm -rf "${ARCHIVE_VERIFY_DIR}"
  ARCHIVE_VERIFY_DIR=""
fi

if command -v sha256sum >/dev/null 2>&1; then
  SHA256="$(sha256sum "${ARCHIVE_PATH}" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
  SHA256="$(shasum -a 256 "${ARCHIVE_PATH}" | awk '{print $1}')"
else
  echo "sha256sum or shasum is required to package the CLI." >&2
  exit 1
fi

printf '%s  %s\n' "${SHA256}" "${ARCHIVE_NAME}" > "${CHECKSUM_PATH}"

echo "${ARCHIVE_PATH}"
echo "${CHECKSUM_PATH}"
