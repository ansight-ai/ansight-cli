#!/usr/bin/env bash
set -euo pipefail

PUBLIC_RELEASE_URL="https://www.ansight.ai/release.json"
PREVIEW_RELEASE_URL="https://www.ansight.ai/preview/release.json"
PUBLIC_DOWNLOAD_BASE_URL="https://ansightaus.blob.core.windows.net/builds/cli"
PREVIEW_DOWNLOAD_BASE_URL="https://ansightaus.blob.core.windows.net/builds/cli/preview"
INSTALLER_URL="${ANSIGHT_INSTALLER_URL:-https://www.ansight.ai/install.sh}"
RECEIPT_PATH="${ANSIGHT_INSTALL_RECEIPT:-}"
CLI_PATH="${ANSIGHT_UPDATE_CLI:-}"
CHANNEL="${ANSIGHT_UPDATE_CHANNEL:-}"
RELEASE_URL="${ANSIGHT_RELEASE_URL:-}"
DOWNLOAD_BASE_URL="${ANSIGHT_DOWNLOAD_BASE_URL:-}"
DOWNLOAD_BASE_URL_OVERRIDDEN=0
if [[ -n "${DOWNLOAD_BASE_URL}" ]]; then
  DOWNLOAD_BASE_URL_OVERRIDDEN=1
fi
TARGET_VERSION="${ANSIGHT_UPDATE_VERSION:-}"
TARGET_BUILD_NUMBER="${ANSIGHT_UPDATE_BUILD_NUMBER:-}"
CHECK_ONLY=0
FORCE_UPDATE=0

usage() {
  cat <<'EOF'
Update an installer-managed Ansight CLI on macOS or Linux.

Usage:
  update.sh [options]

Options:
  --check                       Report the selected update without installing it
  --channel <public|preview>    Select or change the installed update channel
  --version <version>           Select an exact human version string
  --build-number <number>       Select the matching YYYYMMDDNN integer build
  --receipt <path>              Installation receipt to use
  --cli-path <path>             CLI executable used to discover the receipt
  --release-url <url>           Override the selected channel release manifest
  --download-base-url <url>     Override the selected channel archive base URL
  --installer-url <url>         Override the platform installer URL
  --force                       Revalidate and reinstall an already-current build
  --help                        Show this help

Exact selection requires --version and --build-number together. The updater
preserves the receipt's install root, bin directory, channel, and complete custom feeds,
then delegates archive download, SHA-256 validation, health checking, and
activation to install.sh with setup and PATH prompts disabled.
EOF
}

fail() {
  echo "Ansight update failed: $*" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --check)
      CHECK_ONLY=1
      shift
      ;;
    --channel)
      [[ $# -ge 2 ]] || fail "--channel requires a value."
      CHANNEL="$2"
      shift 2
      ;;
    --version)
      [[ $# -ge 2 ]] || fail "--version requires a value."
      TARGET_VERSION="$2"
      shift 2
      ;;
    --build-number)
      [[ $# -ge 2 ]] || fail "--build-number requires a value."
      TARGET_BUILD_NUMBER="$2"
      shift 2
      ;;
    --receipt)
      [[ $# -ge 2 ]] || fail "--receipt requires a value."
      RECEIPT_PATH="$2"
      shift 2
      ;;
    --cli-path)
      [[ $# -ge 2 ]] || fail "--cli-path requires a value."
      CLI_PATH="$2"
      shift 2
      ;;
    --release-url)
      [[ $# -ge 2 ]] || fail "--release-url requires a value."
      RELEASE_URL="$2"
      shift 2
      ;;
    --download-base-url)
      [[ $# -ge 2 ]] || fail "--download-base-url requires a value."
      DOWNLOAD_BASE_URL="${2%/}"
      DOWNLOAD_BASE_URL_OVERRIDDEN=1
      shift 2
      ;;
    --installer-url)
      [[ $# -ge 2 ]] || fail "--installer-url requires a value."
      INSTALLER_URL="$2"
      shift 2
      ;;
    --force)
      FORCE_UPDATE=1
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      fail "Unknown option '$1'."
      ;;
  esac
done

case "$(uname -s)" in
  Darwin|Linux)
    ;;
  *)
    fail "This updater supports macOS and Linux. On Windows, use update.ps1."
    ;;
esac

if [[ -z "${CLI_PATH}" ]]; then
  CLI_PATH="$(command -v ansight 2>/dev/null || true)"
fi
if [[ -z "${RECEIPT_PATH}" && -n "${CLI_PATH}" ]]; then
  VERSION_JSON="$(ANSIGHT_NO_UPDATE_CHECK=1 "${CLI_PATH}" version --json 2>/dev/null || true)"
  RECEIPT_PATH="$(printf '%s\n' "${VERSION_JSON}" \
    | sed -n 's/.*"receiptPath"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    | head -n 1)"
fi
RECEIPT_PATH="${RECEIPT_PATH:-${HOME}/.local/share/ansight/cli/install.json}"
[[ -f "${RECEIPT_PATH}" ]] \
  || fail "No installer-managed CLI receipt was found at ${RECEIPT_PATH}. Run install.sh first or pass --receipt."

json_string_property() {
  local property="$1"
  local file="$2"
  sed -n "s/.*\"${property}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "${file}" \
    | head -n 1
}

json_number_property() {
  local property="$1"
  local file="$2"
  sed -n "s/.*\"${property}\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p" "${file}" \
    | head -n 1
}

receipt_owns_install_root() {
  local receipt_path="$1"
  local install_root="$2"
  local receipt_directory_resolved install_root_resolved
  [[ -d "$(dirname "${receipt_path}")" && -d "${install_root}" ]] || return 1
  receipt_directory_resolved="$(cd "$(dirname "${receipt_path}")" && pwd -P)"
  install_root_resolved="$(cd "${install_root}" && pwd -P)"
  [[ "${receipt_directory_resolved}" == "${install_root_resolved}" ]]
}

RECEIPT_SCHEMA="$(json_string_property schema "${RECEIPT_PATH}")"
[[ "${RECEIPT_SCHEMA}" == "ansight.cli.installation/v1" ]] \
  || fail "${RECEIPT_PATH} is not an Ansight CLI installation receipt."

CURRENT_VERSION="$(json_string_property version "${RECEIPT_PATH}")"
CURRENT_BUILD_NUMBER="$(json_number_property buildNumber "${RECEIPT_PATH}")"
CURRENT_CHANNEL="$(json_string_property channel "${RECEIPT_PATH}")"
INSTALL_ROOT="$(json_string_property installRoot "${RECEIPT_PATH}")"
BIN_DIR="$(json_string_property binDirectory "${RECEIPT_PATH}")"
RECEIPT_RELEASE_URL="$(json_string_property releaseUrl "${RECEIPT_PATH}")"
RECEIPT_DOWNLOAD_BASE_URL="$(json_string_property downloadBaseUrl "${RECEIPT_PATH}")"

[[ -n "${CURRENT_VERSION}" ]] || fail "The installation receipt has no version."
[[ "${CURRENT_BUILD_NUMBER}" =~ ^[0-9]+$ && "${CURRENT_BUILD_NUMBER}" != "0" ]] \
  || fail "The installation receipt has no positive integer buildNumber."
[[ -n "${INSTALL_ROOT}" && -n "${BIN_DIR}" ]] \
  || fail "The installation receipt is missing installRoot or binDirectory."
if [[ "$(basename "${RECEIPT_PATH}")" != "install.json" ]] \
    || ! receipt_owns_install_root "${RECEIPT_PATH}" "${INSTALL_ROOT}"; then
  fail "The receipt installRoot does not own ${RECEIPT_PATH}; refusing to update it."
fi
case "${CURRENT_CHANNEL}" in
  public|preview)
    ;;
  *)
    fail "The installation receipt has an invalid channel '${CURRENT_CHANNEL}'."
    ;;
esac

if [[ -z "${CHANNEL}" ]]; then
  CHANNEL="${CURRENT_CHANNEL}"
fi
CHANNEL="$(printf '%s' "${CHANNEL}" | tr '[:upper:]' '[:lower:]')"
case "${CHANNEL}" in
  public)
    DEFAULT_RELEASE_URL="${PUBLIC_RELEASE_URL}"
    DEFAULT_DOWNLOAD_BASE_URL="${PUBLIC_DOWNLOAD_BASE_URL}"
    ;;
  preview)
    DEFAULT_RELEASE_URL="${PREVIEW_RELEASE_URL}"
    DEFAULT_DOWNLOAD_BASE_URL="${PREVIEW_DOWNLOAD_BASE_URL}"
    ;;
  *)
    fail "--channel must be 'public' or 'preview'."
    ;;
esac

if [[ "${CHANNEL}" == "${CURRENT_CHANNEL}" ]]; then
  RELEASE_URL="${RELEASE_URL:-${RECEIPT_RELEASE_URL:-${DEFAULT_RELEASE_URL}}}"
  DOWNLOAD_BASE_URL="${DOWNLOAD_BASE_URL:-${RECEIPT_DOWNLOAD_BASE_URL:-${DEFAULT_DOWNLOAD_BASE_URL}}}"
else
  RELEASE_URL="${RELEASE_URL:-${DEFAULT_RELEASE_URL}}"
  DOWNLOAD_BASE_URL="${DOWNLOAD_BASE_URL:-${DEFAULT_DOWNLOAD_BASE_URL}}"
fi
if [[ "${DOWNLOAD_BASE_URL_OVERRIDDEN}" -eq 0
      && "${RELEASE_URL}" == "${DEFAULT_RELEASE_URL}"
      && "${DOWNLOAD_BASE_URL}" == file://* ]]; then
  DOWNLOAD_BASE_URL="${DEFAULT_DOWNLOAD_BASE_URL}"
fi
DOWNLOAD_BASE_URL="${DOWNLOAD_BASE_URL%/}"

if [[ -n "${TARGET_VERSION}" && -z "${TARGET_BUILD_NUMBER}"
      || -z "${TARGET_VERSION}" && -n "${TARGET_BUILD_NUMBER}" ]]; then
  fail "Use --version and --build-number together when selecting an exact CLI build."
fi

download_to_stdout() {
  local url="$1"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "${url}"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO- "${url}"
  else
    fail "curl or wget is required."
  fi
}

download_to_file() {
  local url="$1"
  local destination="$2"
  if command -v curl >/dev/null 2>&1; then
    # Curl's standard progress meter includes the total and downloaded sizes.
    # The compact --progress-bar display only reports a percentage.
    curl -fL --retry 3 --retry-delay 1 "${url}" -o "${destination}"
  elif command -v wget >/dev/null 2>&1; then
    wget -O "${destination}" "${url}"
  else
    fail "curl or wget is required."
  fi
}

if [[ -z "${TARGET_VERSION}" ]]; then
  RELEASE_JSON="$(download_to_stdout "${RELEASE_URL}")" \
    || fail "Could not download ${RELEASE_URL}."
  FEED_PRODUCT="$(printf '%s\n' "${RELEASE_JSON}" \
    | sed -n 's/.*"product"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    | head -n 1)"
  if [[ -n "${FEED_PRODUCT}" && "${FEED_PRODUCT}" != "cli" ]]; then
    fail "The release feed describes '${FEED_PRODUCT}', not the Ansight CLI."
  fi
  TARGET_VERSION="$(printf '%s\n' "${RELEASE_JSON}" \
    | sed -n 's/.*"cliVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    | head -n 1)"
  if [[ -z "${TARGET_VERSION}" ]]; then
    TARGET_VERSION="$(printf '%s\n' "${RELEASE_JSON}" \
      | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
      | head -n 1)"
  fi
  TARGET_BUILD_NUMBER="$(printf '%s\n' "${RELEASE_JSON}" \
    | sed -n 's/.*"cliBuildNumber"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' \
    | head -n 1)"
  if [[ -z "${TARGET_BUILD_NUMBER}" ]]; then
    TARGET_BUILD_NUMBER="$(printf '%s\n' "${RELEASE_JSON}" \
      | sed -n 's/.*"buildNumber"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' \
      | head -n 1)"
  fi
  FEED_CHANNEL="$(printf '%s\n' "${RELEASE_JSON}" \
    | sed -n 's/.*"channel"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    | head -n 1)"
  if [[ -n "${FEED_CHANNEL}" && "${FEED_CHANNEL}" != "${CHANNEL}" ]]; then
    fail "The ${CHANNEL} release feed identified itself as '${FEED_CHANNEL}'."
  fi
fi

[[ "${TARGET_VERSION}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] \
  || fail "Invalid target version '${TARGET_VERSION}'."
[[ "${TARGET_BUILD_NUMBER}" =~ ^[0-9]+$ && "${TARGET_BUILD_NUMBER}" != "0" ]] \
  || fail "The selected release has no positive integer buildNumber."

echo "Current: Ansight CLI ${CURRENT_VERSION} (${CURRENT_BUILD_NUMBER}) [${CURRENT_CHANNEL}]"
echo "Target:  Ansight CLI ${TARGET_VERSION} (${TARGET_BUILD_NUMBER}) [${CHANNEL}]"

IS_CURRENT=0
if [[ "${CURRENT_VERSION}" == "${TARGET_VERSION}"
      && "${CURRENT_BUILD_NUMBER}" == "${TARGET_BUILD_NUMBER}"
      && "${CURRENT_CHANNEL}" == "${CHANNEL}" ]]; then
  IS_CURRENT=1
fi

if [[ "${CHECK_ONLY}" -eq 1 ]]; then
  if [[ "${IS_CURRENT}" -eq 1 ]]; then
    echo "Ansight CLI is current."
  else
    echo "An Ansight CLI update or channel change is available."
  fi
  exit 0
fi

if [[ "${IS_CURRENT}" -eq 1 && "${FORCE_UPDATE}" -eq 0 ]]; then
  echo "Ansight CLI is already current."
  exit 0
fi

TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ansight-update.XXXXXX")"
trap 'rm -rf "${TEMP_DIR}"' EXIT
INSTALLER_PATH="${TEMP_DIR}/install.sh"
download_to_file "${INSTALLER_URL}" "${INSTALLER_PATH}" \
  || fail "Could not download the Ansight installer from ${INSTALLER_URL}."

ANSIGHT_INSTALL_ROOT="${INSTALL_ROOT}" \
ANSIGHT_BIN_DIR="${BIN_DIR}" \
ANSIGHT_INSTALL_CHANNEL="${CHANNEL}" \
ANSIGHT_RELEASE_URL="${RELEASE_URL}" \
ANSIGHT_DOWNLOAD_BASE_URL="${DOWNLOAD_BASE_URL}" \
  bash "${INSTALLER_PATH}" \
    --version "${TARGET_VERSION}" \
    --build-number "${TARGET_BUILD_NUMBER}" \
    --channel "${CHANNEL}" \
    --install-dir "${INSTALL_ROOT}" \
    --bin-dir "${BIN_DIR}" \
    --release-url "${RELEASE_URL}" \
    --download-base-url "${DOWNLOAD_BASE_URL}" \
    --no-setup \
    --no-path-update

echo "Ansight CLI update complete. Restart any running Ansight host to use the new build."
